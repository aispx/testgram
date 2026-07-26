using System.Security.Cryptography;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using SchemaMessages = MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 13: Encrypted-file size and descriptor — and
/// Property 14: Reuse of an existing encrypted file.
///
/// <para><b>Property 13.</b> For any set of stored file parts whose declared <c>parts</c> count matches the
/// actual number of saved parts, <c>messages.uploadEncryptedFile</c> stores the opaque blob and returns an
/// <c>encryptedFile</c> whose <c>size</c> equals the total byte length of the stored blob and whose
/// <c>id</c>, <c>access_hash</c>, <c>dc_id</c> and <c>key_fingerprint</c> are populated (id/access_hash
/// non-zero and server-assigned, dc_id identifying the data centre holding the blob, key_fingerprint the
/// client-supplied one, round-tripped verbatim). When the declared part count does NOT match the number of
/// saved parts, <c>FILE_PARTS_INVALID</c> is returned, no encrypted file is stored and no
/// <c>encryptedFile</c> object is returned.
/// <b>Validates: Requirements 11.1, 11.5.</b></para>
///
/// <para><b>Property 14.</b> For any reference to an already stored encrypted file
/// (<c>inputEncryptedFile</c>) supplied to <c>messages.uploadEncryptedFile</c> or
/// <c>messages.sendEncryptedFile</c>, the stored file is REUSED: its contents are not stored a second time
/// and a descriptor referring to the existing file is returned. A reference that does not resolve to a
/// stored encrypted file errors with <c>FILE_EMTPY</c> without storing anything — neither a file nor a
/// message — and delivers no update.
/// <b>Validates: Requirements 7.3, 7.5, 11.4, 11.6.</b></para>
///
/// <para><b>How this is tested.</b> The encrypted-file store is a persistence-level concern (part
/// reassembly, blob length, server-assigned identity, MD5 verification and (id, access_hash) resolution all
/// live in MongoDB), so the first five facts drive the REAL production
/// <see cref="EncryptedFileStore"/> — and, on top of it, the REAL <see cref="SecretChatAppService"/> wired
/// to the REAL <see cref="SecretChatAccessResolver"/> and the REAL TL converters — against a REAL
/// <c>mongod</c> instance started by <see cref="EmbeddedMongoServer"/>. Nothing about blob assembly or
/// descriptor persistence is simulated. The <c>file_parts</c> collection is seeded directly with
/// <see cref="BsonDocument"/>s in exactly the shape
/// <c>MyTelegram.Messenger.Handlers.LatestLayer.Upload.SaveFilePartHandler</c> writes
/// (<c>_id = "{UserId}_{FileId}_{FilePart}"</c>, <c>UserId</c>, <c>FileId</c>, <c>FilePart</c>,
/// <c>Bytes</c>, <c>Size</c>, <c>UploadedAt</c>) and written with the same upsert the handler uses, so the
/// tests consume the very rows a real upload would have produced. When no <c>mongod</c> binary is available
/// those facts skip cleanly via <see cref="RequiresMongoDbFactAttribute"/>.</para>
///
/// <para><b>"Stores the blob" is asserted as durability, not as a write call.</b> After every accepted
/// upload the blob is read back — whole (<c>LoadForDownloadAsync</c>) and through several windows
/// (<c>LoadRangeAsync</c>: the full range, the first byte, the last byte, a middle window, a negative
/// offset, an offset past the end and a zero limit) — and compared with the test's own concatenation, so a
/// descriptor whose <c>size</c> disagreed with the retrievable bytes could not pass. Because the upload
/// staging collection <c>file_parts</c> is keyed by the CLIENT-chosen file id and is UPSERTED, a dedicated
/// fact re-uploads a different payload under the same client file id and asserts the first file still
/// returns its ORIGINAL bytes — i.e. the store keeps an immutable snapshot rather than a live reference.
/// A further fact asserts that a part run with a hole in it (parts that are not a contiguous 0..n-1)
/// is rejected with <c>FILE_PARTS_INVALID</c> and stores nothing, since silently concatenating around a
/// missing part would hand the recipient an undecryptable blob.</para>
///
/// <para><b>What is generated.</b> Because <c>[Property]</c> (FsCheck) and <c>[RequiresMongoDbFact]</c>
/// cannot be combined on one method, the Mongo-backed facts sample the FsCheck generators in
/// <see cref="EncryptedFileGen"/> / <see cref="EncryptedFileArbitraries"/> with <c>Gen.Sample</c> INSIDE the
/// fact and loop over the generated cases (100, or 60 for the facts that perform several uploads per case).
/// Generated: the number of file parts and each part's byte length (so the total blob length differs per
/// case and is never a constant the production code could accidentally match), the client-supplied
/// <c>key_fingerprint</c> (including the extreme <see cref="int"/> values and negatives, so the round-trip
/// through Mongo is real), a declared part count deliberately different from the actual one, and the way a
/// file reference is corrupted (wrong id, wrong access_hash, both, or <c>inputEncryptedFileEmpty</c>).
/// The final fact is a genuine <c>[Property]</c> (<c>MaxTest = 100</c>) driving the real
/// <see cref="SecretChatAppService"/> over the in-memory harness stores, where
/// <see cref="InMemoryEncryptedFileStore.StoreUploadedCallCount"/> /
/// <see cref="InMemoryEncryptedFileStore.ResolveCallCount"/> make "the content is NOT stored again"
/// directly observable.</para>
///
/// <para><b>What is asserted independently.</b> Every expectation is computed from the property statement
/// alone and never read back from the production code: the expected blob is the test's own concatenation of
/// the generated parts in <c>FilePart</c> order, the expected size is <c>Sum(partSizes)</c>, the expected
/// MD5 is computed by the test over that concatenation, and the expected error is fixed by the requirement
/// (<c>FILE_PARTS_INVALID</c> / <c>FILE_EMTPY</c> / <c>MD5_CHECKSUM_INVALID</c>). "Nothing was stored" is
/// asserted as an unchanged document count in the <c>encrypted_files</c> collection (plus the absence of any
/// row for the offending <c>SourceFileId</c>), and "the file was reused" as an unchanged
/// <c>encrypted_files</c> count combined with a returned descriptor identical to the original one.</para>
/// </summary>
public class Property13_EncryptedFileTests
{
    /// <summary>Generated cases per fact — the property runs a minimum of 100 cases where feasible.</summary>
    private const int GeneratedCases = 100;

    /// <summary>Cases for the facts whose single case already performs several uploads.</summary>
    private const int HeavyGeneratedCases = 60;

    /// <summary>FsCheck size parameter for <c>Gen.Sample</c>; every generator here is size-independent.</summary>
    private const int SampleSize = 50;

    /// <summary>The uploader is also the secret-chat caller: <c>file_parts</c> rows are keyed by uploader.</summary>
    private const long UploaderId = SecretChatTestHarness.AdminId;

    /// <summary>A non-default data centre id, so <c>dc_id</c> cannot pass by accidentally being the fallback.</summary>
    private const int ConfiguredDcId = 3;

    private const string EncryptedFilesCollection = "encrypted_files";
    private const string FilePartsCollection = "file_parts";

    // ==============================================================================================
    // Property 13 — size and descriptor of a freshly uploaded encrypted file (Requirement 11.1).
    // ==============================================================================================

    /// <summary>
    /// Requirement 11.1: for every generated set of saved parts whose declared count matches, the store
    /// persists the blob and returns a descriptor whose <c>size</c> equals the total byte length of the
    /// stored blob and whose <c>id</c>, <c>access_hash</c>, <c>dc_id</c> and <c>key_fingerprint</c> are
    /// populated. The same is then asserted end-to-end through the real
    /// <see cref="SecretChatAppService.UploadEncryptedFileAsync"/>, whose TL <c>encryptedFile</c> must carry
    /// exactly those values. The stored blob is additionally read back and compared byte-for-byte with the
    /// test's own concatenation of the generated parts (Requirement 11.2 — opaque, uninspected relay).
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Uploaded_file_size_equals_the_stored_blob_and_the_descriptor_is_populated()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new EncryptedFileStore(mongo.Database, TestOptions(ConfiguredDcId));
        var world = SecretChatWorld.Create(store);

        var cases = Sample(EncryptedFileArbitraries.UploadCase().Generator, GeneratedCases);
        var assignedIds = new List<long>();
        var assignedAccessHashes = new List<long>();

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var because = $"case #{i} {@case}";

            // Expected blob and size are the TEST's own values, derived from the generated case alone.
            var parts = BuildParts(@case.PartSizes, seed: i);
            var expectedBlob = Concat(parts);
            var expectedSize = @case.PartSizes.Aggregate(0L, (acc, s) => acc + s);
            expectedBlob.LongLength.ShouldBe(expectedSize, because);

            var storeFileId = 700_000L + i;
            await SeedPartsAsync(mongo.Database, UploaderId, storeFileId, parts);

            var before = await CountEncryptedFilesAsync(mongo.Database);

            var descriptor = await store.StoreUploadedAsync(UploaderId,
                storeFileId,
                declaredParts: @case.PartSizes.Length,
                @case.KeyFingerprint,
                md5Checksum: null);

            // Requirement 11.1: size == total byte length of the stored blob.
            descriptor.Size.ShouldBe(expectedSize, because);

            // Requirement 11.1: id / access_hash are server-assigned and populated (never the 0 default).
            descriptor.Id.ShouldNotBe(0, because);
            descriptor.AccessHash.ShouldNotBe(0, because);

            // Requirement 11.1: dc_id identifies the data centre holding the blob.
            descriptor.DcId.ShouldBeGreaterThanOrEqualTo(1, because);
            descriptor.DcId.ShouldBe(ConfiguredDcId, because);

            // Requirement 11.1: the client-supplied key_fingerprint round-trips verbatim.
            descriptor.KeyFingerprint.ShouldBe(@case.KeyFingerprint, because);

            // Exactly one encrypted file was stored, and it describes THIS upload.
            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before + 1, because);
            var stored = await FindEncryptedFileAsync(mongo.Database, descriptor.Id);
            stored.ShouldNotBeNull(because);
            stored!["AccessHash"].AsInt64.ShouldBe(descriptor.AccessHash, because);
            stored["Size"].AsInt64.ShouldBe(expectedSize, because);
            stored["DcId"].AsInt32.ShouldBe(descriptor.DcId, because);
            stored["KeyFingerprint"].AsInt32.ShouldBe(@case.KeyFingerprint, because);
            stored["OwnerUserId"].AsInt64.ShouldBe(UploaderId, because);
            stored["SourceFileId"].AsInt64.ShouldBe(storeFileId, because);
            stored["Parts"].AsInt32.ShouldBe(@case.PartSizes.Length, because);

            // The descriptor is addressable by (id, access_hash) and only by the right pair.
            var resolved = await store.ResolveAsync(descriptor.Id, descriptor.AccessHash);
            resolved.ShouldBe(descriptor, because);
            (await store.ResolveAsync(descriptor.Id, descriptor.AccessHash + 1)).ShouldBeNull(because);

            // Requirement 11.2: the blob is relayed back byte-for-byte — nothing was inspected or rewritten.
            var download = await store.LoadForDownloadAsync(descriptor.Id, descriptor.AccessHash);
            download.ShouldNotBeNull(because);
            download!.Value.Blob.ShouldBe(expectedBlob, because);
            download.Value.Document.Size.ShouldBe(expectedSize, because);

            // The declared size really is the retrievable size: every window of the blob reads back exactly
            // the corresponding slice of the test's own concatenation.
            await AssertRangeAsync(store, descriptor, expectedBlob, offset: 0, limit: (int)expectedSize, because);
            await AssertRangeAsync(store, descriptor, expectedBlob, offset: 0, limit: 1, because);
            await AssertRangeAsync(store, descriptor, expectedBlob, expectedSize - 1, limit: 5, because);
            await AssertRangeAsync(store, descriptor, expectedBlob, expectedSize / 2,
                Math.Max(1, (int)(expectedSize / 2)), because);
            // A negative offset is clamped to the start of the blob.
            await AssertRangeAsync(store, descriptor, expectedBlob, offset: -5, limit: (int)expectedSize, because);

            // Past the end, or a zero-length window, yields no bytes (but still finds the file).
            (await store.LoadRangeAsync(descriptor.Id, descriptor.AccessHash, expectedSize, 16))!
                .Value.Bytes.ShouldBeEmpty(because);
            (await store.LoadRangeAsync(descriptor.Id, descriptor.AccessHash, 0, 0))!
                .Value.Bytes.ShouldBeEmpty(because);

            // The blob is reachable only through the right capability pair.
            (await store.LoadRangeAsync(descriptor.Id, descriptor.AccessHash + 1, 0, 16)).ShouldBeNull(because);
            (await store.LoadForDownloadAsync(descriptor.Id, descriptor.AccessHash + 1)).ShouldBeNull(because);

            assignedIds.Add(descriptor.Id);
            assignedAccessHashes.Add(descriptor.AccessHash);

            // ... and the same statement through the real service: messages.uploadEncryptedFile returns a TL
            // encryptedFile carrying exactly the descriptor of the blob it just stored.
            var serviceFileId = 800_000L + i;
            await SeedPartsAsync(mongo.Database, UploaderId, serviceFileId, parts);

            var uploaded = await world.Service.UploadEncryptedFileAsync(world.AdminInput,
                world.Peer,
                new TInputEncryptedFileUploaded
                {
                    Id = serviceFileId,
                    Parts = @case.PartSizes.Length,
                    KeyFingerprint = @case.KeyFingerprint,
                    Md5Checksum = string.Empty
                });

            var tlFile = uploaded.ShouldBeOfType<TEncryptedFile>();
            tlFile.Size.ShouldBe(expectedSize, because);
            tlFile.Id.ShouldNotBe(0, because);
            tlFile.AccessHash.ShouldNotBe(0, because);
            tlFile.DcId.ShouldBe(ConfiguredDcId, because);
            tlFile.KeyFingerprint.ShouldBe(@case.KeyFingerprint, because);

            (await store.ResolveAsync(tlFile.Id, tlFile.AccessHash))
                .ShouldBe(new EncryptedFileDescriptor(tlFile.Id, tlFile.AccessHash, expectedSize, ConfiguredDcId,
                        @case.KeyFingerprint),
                    because);

            assignedIds.Add(tlFile.Id);
            assignedAccessHashes.Add(tlFile.AccessHash);
        }

        // Server-assigned identity is per-file: no two uploads share an id (or an access_hash).
        assignedIds.Distinct().Count().ShouldBe(assignedIds.Count);
        assignedAccessHashes.Distinct().Count().ShouldBe(assignedAccessHashes.Count);

        // dc_id is read from configuration, with 1 as the documented fallback — still a valid data centre.
        var fallbackStore = new EncryptedFileStore(mongo.Database, TestOptions(thisDcId: 0));
        await SeedPartsAsync(mongo.Database, UploaderId, 999_001L, BuildParts([7, 9], seed: 4242));
        var fallbackDescriptor = await fallbackStore.StoreUploadedAsync(UploaderId, 999_001L, 2, 55, null);
        fallbackDescriptor.DcId.ShouldBe(1);
        fallbackDescriptor.Size.ShouldBe(16);
    }

    // ==============================================================================================
    // Property 13 — declared part count mismatch (Requirement 11.5) and the empty file (Requirement 11.6).
    // ==============================================================================================

    /// <summary>
    /// Requirement 11.5: for every generated declared part count that differs from the number of saved
    /// parts, the upload fails with <c>FILE_PARTS_INVALID</c>, stores no encrypted file and returns no
    /// <c>encryptedFile</c> object — asserted both at the store level and through the real
    /// <see cref="SecretChatAppService.UploadEncryptedFileAsync"/>. A file with no saved parts at all fails
    /// with <c>FILE_EMTPY</c> and likewise stores nothing.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Declared_part_count_mismatch_returns_FILE_PARTS_INVALID_and_stores_no_file()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new EncryptedFileStore(mongo.Database, TestOptions(ConfiguredDcId));
        var world = SecretChatWorld.Create(store);

        var cases = Sample(EncryptedFileArbitraries.PartsMismatchCase().Generator, GeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var because = $"case #{i} {@case}";
            @case.DeclaredParts.ShouldNotBe(@case.PartSizes.Length, because);
            @case.DeclaredParts.ShouldBeGreaterThan(0, because);

            var fileId = 1_100_000L + i;
            await SeedPartsAsync(mongo.Database, UploaderId, fileId, BuildParts(@case.PartSizes, seed: i));

            var before = await CountEncryptedFilesAsync(mongo.Database);

            // Store level.
            (await Should.ThrowAsync<RpcException>(async () => await store.StoreUploadedAsync(UploaderId,
                    fileId,
                    @case.DeclaredParts,
                    @case.KeyFingerprint,
                    md5Checksum: null)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.FilePartsInvalid, because);

            // Handler level — the RPC surfaces the same error and returns no encryptedFile object.
            (await Should.ThrowAsync<RpcException>(async () => await world.Service.UploadEncryptedFileAsync(
                    world.AdminInput,
                    world.Peer,
                    new TInputEncryptedFileUploaded
                    {
                        Id = fileId,
                        Parts = @case.DeclaredParts,
                        KeyFingerprint = @case.KeyFingerprint,
                        Md5Checksum = string.Empty
                    })))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.FilePartsInvalid, because);

            // Nothing was stored by either attempt.
            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before, because);
            (await CountEncryptedFilesForSourceAsync(mongo.Database, fileId)).ShouldBe(0, because);

            // The declared count that DOES match still succeeds — the property is not vacuously true because
            // uploads always fail.
            var ok = await store.StoreUploadedAsync(UploaderId, fileId, @case.PartSizes.Length,
                @case.KeyFingerprint, null);
            ok.Size.ShouldBe(@case.PartSizes.Aggregate(0L, (acc, s) => acc + s), because);
            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before + 1, because);
        }

        // Requirement 11.6 / FILE_EMTPY: an upload whose file has no saved parts stores nothing, whatever
        // part count it declares (including the "unspecified" 0).
        var emptyCases = Sample(EncryptedFileGen.DeclaredPartsForEmptyFile, GeneratedCases);
        for (var i = 0; i < emptyCases.Count; i++)
        {
            var declared = emptyCases[i];
            var because = $"empty-file case #{i} (declaredParts={declared})";
            var neverSeededFileId = 2_100_000L + i;
            var before = await CountEncryptedFilesAsync(mongo.Database);

            (await Should.ThrowAsync<RpcException>(async () => await store.StoreUploadedAsync(UploaderId,
                    neverSeededFileId,
                    declared,
                    keyFingerprint: 1,
                    md5Checksum: null)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.FileEmtpy, because);

            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before, because);
            (await CountEncryptedFilesForSourceAsync(mongo.Database, neverSeededFileId)).ShouldBe(0, because);
        }

        // Requirement 11.5, the other way a part run can be wrong: the saved parts do NOT form a contiguous
        // 0..n-1 run. Concatenating around the hole would produce a shorter blob the recipient cannot
        // decrypt, so the upload must be rejected and nothing stored — even though the COUNT matches.
        var gapCases = Sample(EncryptedFileArbitraries.GapCase().Generator, GeneratedCases);
        for (var i = 0; i < gapCases.Count; i++)
        {
            var @case = gapCases[i];
            var because = $"gap case #{i} {@case}";
            var fileId = 5_100_000L + i;

            // Indices 0..count, minus the missing one: exactly `count` parts with a hole in the run.
            var indices = Enumerable.Range(0, @case.PartSizes.Length + 1)
                .Where(index => index != @case.MissingIndex)
                .ToArray();
            indices.Length.ShouldBe(@case.PartSizes.Length, because);

            await SeedPartsAsync(mongo.Database, UploaderId, fileId, BuildParts(@case.PartSizes, seed: i), indices);

            var before = await CountEncryptedFilesAsync(mongo.Database);

            (await Should.ThrowAsync<RpcException>(async () => await store.StoreUploadedAsync(UploaderId,
                    fileId,
                    // The declared count MATCHES, so only the contiguity check can reject this.
                    @case.PartSizes.Length,
                    @case.KeyFingerprint,
                    md5Checksum: null)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.FilePartsInvalid, because);

            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before, because);
            (await CountEncryptedFilesForSourceAsync(mongo.Database, fileId)).ShouldBe(0, because);
        }
    }

    // ==============================================================================================
    // Property 13 — "stores the blob" means an immutable snapshot, not a live reference.
    // ==============================================================================================

    /// <summary>
    /// Requirement 11.1 ("THE Encrypted_File_Store SHALL store the encrypted file blob"): the upload staging
    /// collection <c>file_parts</c> is keyed by the CLIENT-chosen file id and is upserted by
    /// <c>upload.saveFilePart</c>, so a client is free to reuse a file id for a completely different file.
    /// A stored encrypted file must therefore keep the bytes it was stored with: after a second, different
    /// upload under the SAME client file id, the first file still reports its original size and still
    /// downloads its ORIGINAL bytes, and the second file downloads the new ones.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_stored_file_keeps_its_original_bytes_when_the_client_reuses_the_upload_file_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new EncryptedFileStore(mongo.Database, TestOptions(ConfiguredDcId));

        var cases = Sample(EncryptedFileArbitraries.UploadCase().Generator, HeavyGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var because = $"case #{i} {@case}";

            // ONE client file id, reused for two genuinely different payloads of the same part count —
            // exactly what upload.saveFilePart's upsert allows.
            var clientFileId = 6_100_000L + i;
            var firstParts = BuildParts(@case.PartSizes, seed: i + 31_000);
            var secondParts = BuildParts(@case.PartSizes, seed: i + 61_000);
            var firstBlob = Concat(firstParts);
            var secondBlob = Concat(secondParts);
            firstBlob.ShouldNotBe(secondBlob, because);

            await SeedPartsAsync(mongo.Database, UploaderId, clientFileId, firstParts);
            var first = await store.StoreUploadedAsync(UploaderId, clientFileId, @case.PartSizes.Length,
                @case.KeyFingerprint, null);

            // The client re-uploads a DIFFERENT file under the same file id (upsert over the same rows).
            await SeedPartsAsync(mongo.Database, UploaderId, clientFileId, secondParts);
            var second = await store.StoreUploadedAsync(UploaderId, clientFileId, @case.PartSizes.Length,
                @case.KeyFingerprint, null);

            second.Id.ShouldNotBe(first.Id, because);

            // The first file is untouched: same descriptor, same size, same bytes.
            (await store.ResolveAsync(first.Id, first.AccessHash)).ShouldBe(first, because);

            var firstDownload = await store.LoadForDownloadAsync(first.Id, first.AccessHash);
            firstDownload.ShouldNotBeNull(because);
            firstDownload!.Value.Blob.ShouldBe(firstBlob, because);

            // ... and the second file carries the new bytes.
            var secondDownload = await store.LoadForDownloadAsync(second.Id, second.AccessHash);
            secondDownload.ShouldNotBeNull(because);
            secondDownload!.Value.Blob.ShouldBe(secondBlob, because);

            // Ranged reads of the first file are equally unaffected by the overwrite.
            await AssertRangeAsync(store, first, firstBlob, offset: 0, limit: (int)first.Size, because);
            await AssertRangeAsync(store, first, firstBlob, first.Size / 2,
                Math.Max(1, (int)(first.Size / 2)), because);
        }
    }

    // ==============================================================================================
    // Property 13 — the optional MD5 is verified against the assembled blob.
    // ==============================================================================================

    /// <summary>
    /// Requirement 11.1 (content check): when the client transmits an <c>md5_checksum</c>, it is verified
    /// against the assembled blob before the file is stored. The correct checksum — computed by the TEST as
    /// lowercase hex of MD5 over its own concatenation of the generated parts — is accepted and yields the
    /// same descriptor as an unchecked upload; a wrong checksum yields <c>MD5_CHECKSUM_INVALID</c> and
    /// stores nothing.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_supplied_md5_checksum_is_verified_against_the_assembled_blob()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new EncryptedFileStore(mongo.Database, TestOptions(ConfiguredDcId));

        var cases = Sample(EncryptedFileArbitraries.UploadCase().Generator, HeavyGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var because = $"case #{i} {@case}";

            var parts = BuildParts(@case.PartSizes, seed: i + 9_000);
            var expectedBlob = Concat(parts);
            var expectedSize = @case.PartSizes.Aggregate(0L, (acc, s) => acc + s);
            var correctMd5 = Convert.ToHexString(MD5.HashData(expectedBlob)).ToLowerInvariant();
            var wrongMd5 = Corrupt(correctMd5);

            var fileId = 3_100_000L + i;
            await SeedPartsAsync(mongo.Database, UploaderId, fileId, parts);

            var before = await CountEncryptedFilesAsync(mongo.Database);

            // A wrong checksum is rejected and stores nothing.
            (await Should.ThrowAsync<RpcException>(async () => await store.StoreUploadedAsync(UploaderId,
                    fileId,
                    @case.PartSizes.Length,
                    @case.KeyFingerprint,
                    wrongMd5)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.Md5ChecksumInvalid, because);

            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before, because);
            (await CountEncryptedFilesForSourceAsync(mongo.Database, fileId)).ShouldBe(0, because);

            // The correct checksum is accepted and produces the descriptor of the very same blob.
            var descriptor = await store.StoreUploadedAsync(UploaderId,
                fileId,
                @case.PartSizes.Length,
                @case.KeyFingerprint,
                correctMd5);

            descriptor.Size.ShouldBe(expectedSize, because);
            descriptor.KeyFingerprint.ShouldBe(@case.KeyFingerprint, because);
            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(before + 1, because);

            var download = await store.LoadForDownloadAsync(descriptor.Id, descriptor.AccessHash);
            download.ShouldNotBeNull(because);
            download!.Value.Blob.ShouldBe(expectedBlob, because);
        }
    }

    // ==============================================================================================
    // Property 14 — reuse of an already stored encrypted file (Requirements 7.3, 7.5, 11.4, 11.6).
    // ==============================================================================================

    /// <summary>
    /// Requirements 11.4 and 7.5: a reference to an already stored encrypted file is REUSED — both
    /// <c>messages.uploadEncryptedFile</c> and <c>messages.sendEncryptedFile</c> return a descriptor
    /// referring to the existing file, and the <c>encrypted_files</c> collection does not grow (the contents
    /// are not stored a second time). Requirements 11.6 and 7.3: a reference that does not resolve — a wrong
    /// id, a wrong access_hash, both, or <c>inputEncryptedFileEmpty</c> — errors with <c>FILE_EMTPY</c>,
    /// stores no encrypted file, stores no message and delivers no update.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_existing_encrypted_file_is_reused_and_an_unresolvable_reference_stores_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new EncryptedFileStore(mongo.Database, TestOptions(ConfiguredDcId));

        var cases = Sample(EncryptedFileArbitraries.ReferenceCase().Generator, HeavyGeneratedCases);

        for (var i = 0; i < cases.Count; i++)
        {
            var @case = cases[i];
            var because = $"case #{i} {@case}";
            var world = SecretChatWorld.Create(store);

            var parts = BuildParts(@case.PartSizes, seed: i + 17_000);
            var expectedSize = @case.PartSizes.Aggregate(0L, (acc, s) => acc + s);
            var fileId = 4_100_000L + i;
            await SeedPartsAsync(mongo.Database, UploaderId, fileId, parts);

            // The file is uploaded ONCE.
            var original = (await world.Service.UploadEncryptedFileAsync(world.AdminInput,
                world.Peer,
                new TInputEncryptedFileUploaded
                {
                    Id = fileId,
                    Parts = @case.PartSizes.Length,
                    KeyFingerprint = @case.KeyFingerprint,
                    Md5Checksum = string.Empty
                })).ShouldBeOfType<TEncryptedFile>();

            original.Size.ShouldBe(expectedSize, because);

            var filesAfterUpload = await CountEncryptedFilesAsync(mongo.Database);
            var partsAfterUpload = await CountFilePartsAsync(mongo.Database);

            // ---- reuse (Requirements 11.4 / 7.5) -------------------------------------------------
            var reference = new TInputEncryptedFile { Id = original.Id, AccessHash = original.AccessHash };

            var reuploaded = (await world.Service.UploadEncryptedFileAsync(world.AdminInput, world.Peer, reference))
                .ShouldBeOfType<TEncryptedFile>();

            // The descriptor REFERS TO the existing file — same identity, same size, same fingerprint.
            reuploaded.Id.ShouldBe(original.Id, because);
            reuploaded.AccessHash.ShouldBe(original.AccessHash, because);
            reuploaded.Size.ShouldBe(original.Size, because);
            reuploaded.DcId.ShouldBe(original.DcId, because);
            reuploaded.KeyFingerprint.ShouldBe(original.KeyFingerprint, because);

            // ... and the contents were NOT stored a second time.
            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(filesAfterUpload, because);
            (await CountFilePartsAsync(mongo.Database)).ShouldBe(partsAfterUpload, because);

            var sent = (await world.Service.SendEncryptedFileAsync(world.AdminInput,
                world.Peer,
                randomId: 5_000 + i,
                new byte[] { 1, 2, 3 },
                reference,
                silent: false)).ShouldBeOfType<SchemaMessages.TSentEncryptedFile>();

            var sentFile = sent.File.ShouldBeOfType<TEncryptedFile>();
            sentFile.Id.ShouldBe(original.Id, because);
            sentFile.AccessHash.ShouldBe(original.AccessHash, because);
            sentFile.Size.ShouldBe(original.Size, because);
            sentFile.KeyFingerprint.ShouldBe(original.KeyFingerprint, because);

            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(filesAfterUpload, because);
            (await CountFilePartsAsync(mongo.Database)).ShouldBe(partsAfterUpload, because);
            world.Messages.All.Count.ShouldBe(1, because);
            world.Dispatcher.Dispatched.Count.ShouldBe(1, because);

            // ---- unresolvable reference (Requirements 11.6 / 7.3) ---------------------------------
            var broken = BuildReference(@case.Corruption, original.Id, original.AccessHash);

            (await Should.ThrowAsync<RpcException>(async () =>
                    await world.Service.UploadEncryptedFileAsync(world.AdminInput, world.Peer, broken)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.FileEmtpy, because);

            (await Should.ThrowAsync<RpcException>(async () => await world.Service.SendEncryptedFileAsync(
                    world.AdminInput,
                    world.Peer,
                    randomId: 6_000 + i,
                    new byte[] { 4, 5, 6 },
                    broken,
                    silent: false)))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.FileEmtpy, because);

            // Nothing was stored and nothing was delivered by the failed attempts.
            (await CountEncryptedFilesAsync(mongo.Database)).ShouldBe(filesAfterUpload, because);
            (await CountFilePartsAsync(mongo.Database)).ShouldBe(partsAfterUpload, because);
            world.Messages.All.Count.ShouldBe(1, because);
            world.Dispatcher.Dispatched.Count.ShouldBe(1, because);
        }
    }

    // ==============================================================================================
    // Property 14 — reuse never triggers a second store (observable call counts, in-memory).
    // ==============================================================================================

    /// <summary>
    /// Requirements 7.5 / 11.4 stated as the store interaction itself, and Requirements 7.3 / 11.6 as the
    /// absence of any effect: driving the real <see cref="SecretChatAppService"/> over the harness stores,
    /// a resolvable <c>inputEncryptedFile</c> is served purely by
    /// <see cref="IEncryptedFileStore.ResolveAsync"/> — <see cref="IEncryptedFileStore.StoreUploadedAsync"/>
    /// is never called a second time — while an unresolvable one raises <c>FILE_EMTPY</c> and leaves the
    /// message store and the update dispatcher untouched.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(EncryptedFileArbitraries) }, MaxTest = 100)]
    public void Reusing_a_stored_file_never_stores_it_again_and_a_bad_reference_stores_no_message(
        EncryptedFileReuseCase @case)
    {
        var because = @case.ToString();
        var fileStore = new InMemoryEncryptedFileStore();
        var world = SecretChatWorld.Create(fileStore);
        var input = @case.CallerIsAdmin ? world.AdminInput : world.ParticipantInput;

        const long clientFileId = 4242;
        var parts = BuildParts(@case.PartSizes, seed: 3);
        var expectedSize = @case.PartSizes.Aggregate(0L, (acc, s) => acc + s);
        fileStore.Parts[(input.UserId, clientFileId)] = parts;

        // The one and only genuine upload.
        var original = world.Service.UploadEncryptedFileAsync(input,
                world.Peer,
                new TInputEncryptedFileUploaded
                {
                    Id = clientFileId,
                    Parts = @case.PartSizes.Length,
                    KeyFingerprint = @case.KeyFingerprint,
                    Md5Checksum = string.Empty
                })
            .GetAwaiter().GetResult()
            .ShouldBeOfType<TEncryptedFile>();

        original.Size.ShouldBe(expectedSize, because);
        original.KeyFingerprint.ShouldBe(@case.KeyFingerprint, because);
        fileStore.StoreUploadedCallCount.ShouldBe(1, because);
        fileStore.ResolveCallCount.ShouldBe(0, because);

        var reference = BuildReference(@case.Corruption, original.Id, original.AccessHash);
        // A reference that is not an inputEncryptedFile never reaches the store at all.
        var expectedResolveCalls = @case.Corruption == EncryptedFileCorruption.EmptyInput ? 0 : 1;

        if (@case.Corruption == EncryptedFileCorruption.None)
        {
            // Requirement 11.4: uploadEncryptedFile returns the EXISTING file's descriptor ...
            var reuploaded = world.Service.UploadEncryptedFileAsync(input, world.Peer, reference)
                .GetAwaiter().GetResult()
                .ShouldBeOfType<TEncryptedFile>();

            reuploaded.Id.ShouldBe(original.Id, because);
            reuploaded.AccessHash.ShouldBe(original.AccessHash, because);
            reuploaded.Size.ShouldBe(original.Size, because);
            reuploaded.DcId.ShouldBe(original.DcId, because);
            reuploaded.KeyFingerprint.ShouldBe(original.KeyFingerprint, because);

            // ... without storing the contents again.
            fileStore.StoreUploadedCallCount.ShouldBe(1, because);
            fileStore.ResolveCallCount.ShouldBe(1, because);

            // Requirement 7.5: the same holds for sendEncryptedFile.
            var sent = world.Service.SendEncryptedFileAsync(input,
                    world.Peer,
                    @case.SendRandomId,
                    new byte[] { 7, 7, 7 },
                    reference,
                    @case.Silent)
                .GetAwaiter().GetResult()
                .ShouldBeOfType<SchemaMessages.TSentEncryptedFile>();

            var sentFile = sent.File.ShouldBeOfType<TEncryptedFile>();
            sentFile.Id.ShouldBe(original.Id, because);
            sentFile.AccessHash.ShouldBe(original.AccessHash, because);
            sentFile.Size.ShouldBe(original.Size, because);
            sentFile.KeyFingerprint.ShouldBe(original.KeyFingerprint, because);

            fileStore.StoreUploadedCallCount.ShouldBe(1, because);
            fileStore.ResolveCallCount.ShouldBe(2, because);

            // Requirement 7.2: the message carries the reused file, delivered once.
            var stored = world.Messages.All.ShouldHaveSingleItem();
            stored.RandomId.ShouldBe(@case.SendRandomId, because);
            stored.File.ShouldNotBeNull(because);

            var dispatched = world.Dispatcher.Dispatched.ShouldHaveSingleItem();
            var update = dispatched.Update.ShouldBeOfType<TUpdateNewEncryptedMessage>();
            var deliveredFile = update.Message.ShouldBeOfType<TEncryptedMessage>().File
                .ShouldBeOfType<TEncryptedFile>();
            deliveredFile.Id.ShouldBe(original.Id, because);
            deliveredFile.AccessHash.ShouldBe(original.AccessHash, because);
            deliveredFile.Size.ShouldBe(original.Size, because);
            deliveredFile.KeyFingerprint.ShouldBe(original.KeyFingerprint, because);

            return;
        }

        // Requirement 11.6: uploadEncryptedFile with an unresolvable reference errors and stores nothing.
        Should.Throw<RpcException>(() => world.Service
                .UploadEncryptedFileAsync(input, world.Peer, reference)
                .GetAwaiter().GetResult())
            .RpcError.ShouldBe(RpcErrors.RpcErrors400.FileEmtpy, because);

        fileStore.StoreUploadedCallCount.ShouldBe(1, because);
        fileStore.ResolveCallCount.ShouldBe(expectedResolveCalls, because);

        // Requirement 7.3: sendEncryptedFile likewise errors, stores no message and delivers nothing.
        Should.Throw<RpcException>(() => world.Service
                .SendEncryptedFileAsync(input,
                    world.Peer,
                    @case.SendRandomId,
                    new byte[] { 7, 7, 7 },
                    reference,
                    @case.Silent)
                .GetAwaiter().GetResult())
            .RpcError.ShouldBe(RpcErrors.RpcErrors400.FileEmtpy, because);

        fileStore.StoreUploadedCallCount.ShouldBe(1, because);
        fileStore.ResolveCallCount.ShouldBe(expectedResolveCalls * 2, because);
        world.Messages.All.ShouldBeEmpty(because);
        world.Dispatcher.Dispatched.ShouldBeEmpty(because);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The real <see cref="SecretChatAppService"/> over an Established chat, with a real resolver.</summary>
    private sealed class SecretChatWorld
    {
        public required SecretChatAppService Service { get; init; }
        public required RecordingUpdateDispatcher Dispatcher { get; init; }
        public required InMemorySecretChatMessageStore Messages { get; init; }
        public required TestRequestInput AdminInput { get; init; }
        public required TestRequestInput ParticipantInput { get; init; }
        public required IInputEncryptedChat Peer { get; init; }

        public static SecretChatWorld Create(IEncryptedFileStore fileStore)
        {
            var queryProcessor = new FakeQueryProcessor();
            queryProcessor.Users[SecretChatTestHarness.AdminId] = FakeUser.Create(SecretChatTestHarness.AdminId);
            queryProcessor.Users[SecretChatTestHarness.ParticipantId] =
                FakeUser.Create(SecretChatTestHarness.ParticipantId);
            queryProcessor.Chats[SecretChatTestHarness.ChatId] = SecretChatTestHarness.Chat();

            var dispatcher = new RecordingUpdateDispatcher();
            var messages = new InMemorySecretChatMessageStore();

            return new SecretChatWorld
            {
                Service = new SecretChatAppService(new RecordingCommandBus(),
                    queryProcessor,
                    new FakeIdGenerator(),
                    new FakeBlockCacheAppService(),
                    new SecretChatAccessResolver(queryProcessor),
                    dispatcher,
                    messages,
                    new InMemorySecretChatRequestLedger(),
                    fileStore,
                    SecretChatTestHarness.ChatConverters(),
                    SecretChatTestHarness.MessageConverters(),
                    SecretChatTestHarness.FileConverters()),
                Dispatcher = dispatcher,
                Messages = messages,
                AdminInput = SecretChatTestHarness.Input(SecretChatTestHarness.AdminId,
                    SecretChatTestHarness.AdminPermAuthKeyId),
                ParticipantInput = SecretChatTestHarness.Input(SecretChatTestHarness.ParticipantId,
                    SecretChatTestHarness.ParticipantPermAuthKeyId),
                Peer = SecretChatTestHarness.InputChat()
            };
        }
    }

    /// <summary>Builds the <c>inputEncryptedFile</c> the case describes, corrupted as requested.</summary>
    private static IInputEncryptedFile BuildReference(EncryptedFileCorruption corruption, long id, long accessHash)
    {
        return corruption switch
        {
            EncryptedFileCorruption.None => new TInputEncryptedFile { Id = id, AccessHash = accessHash },
            EncryptedFileCorruption.WrongAccessHash => new TInputEncryptedFile
            {
                Id = id,
                AccessHash = accessHash ^ 0x5f5f5f5f
            },
            EncryptedFileCorruption.WrongId => new TInputEncryptedFile
            {
                Id = id ^ 0x5f5f5f5f,
                AccessHash = accessHash
            },
            EncryptedFileCorruption.WrongIdAndAccessHash => new TInputEncryptedFile
            {
                Id = id ^ 0x5f5f5f5f,
                AccessHash = accessHash ^ 0x5f5f5f5f
            },
            _ => new TInputEncryptedFileEmpty()
        };
    }

    /// <summary>
    /// Writes the generated parts into <c>file_parts</c> in exactly the shape — and with exactly the upsert —
    /// <c>upload.saveFilePart</c> persists them, so re-seeding the same (user, file id) overwrites the rows
    /// just as a client reusing a file id would. <paramref name="partIndices"/> defaults to the contiguous
    /// run 0..n-1; passing a different set produces the holes the contiguity check must reject.
    /// </summary>
    private static async Task SeedPartsAsync(IMongoDatabase database,
        long userId,
        long fileId,
        IReadOnlyList<byte[]> parts,
        IReadOnlyList<int>? partIndices = null)
    {
        var collection = database.GetCollection<BsonDocument>(FilePartsCollection);
        for (var i = 0; i < parts.Count; i++)
        {
            var partIndex = partIndices?[i] ?? i;
            var document = new BsonDocument
            {
                ["_id"] = $"{userId}_{fileId}_{partIndex}",
                ["UserId"] = userId,
                ["FileId"] = fileId,
                ["FilePart"] = partIndex,
                ["Bytes"] = parts[i],
                ["Size"] = parts[i].Length,
                ["UploadedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await collection.ReplaceOneAsync(Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
                document,
                new ReplaceOptions { IsUpsert = true });
        }
    }

    /// <summary>
    /// Asserts that the window <c>[offset, offset + limit)</c> of a stored file reads back exactly the
    /// corresponding slice of the test's own expected blob (negative offsets clamp to 0, the length clamps
    /// to the end of the blob).
    /// </summary>
    private static async Task AssertRangeAsync(EncryptedFileStore store,
        EncryptedFileDescriptor descriptor,
        byte[] expectedBlob,
        long offset,
        int limit,
        string because)
    {
        var range = await store.LoadRangeAsync(descriptor.Id, descriptor.AccessHash, offset, limit);
        range.ShouldNotBeNull(because);

        var start = (int)Math.Max(0, offset);
        var length = Math.Min(limit, expectedBlob.Length - start);
        range!.Value.Document.Id.ShouldBe(descriptor.Id, because);
        range.Value.Bytes.ShouldBe(expectedBlob.Skip(start).Take(length).ToArray(),
            $"{because}, range(offset={offset}, limit={limit})");
    }

    private static Task<long> CountEncryptedFilesAsync(IMongoDatabase database)
    {
        return database.GetCollection<BsonDocument>(EncryptedFilesCollection)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
    }

    private static Task<long> CountEncryptedFilesForSourceAsync(IMongoDatabase database, long sourceFileId)
    {
        return database.GetCollection<BsonDocument>(EncryptedFilesCollection)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("SourceFileId", sourceFileId));
    }

    private static Task<long> CountFilePartsAsync(IMongoDatabase database)
    {
        return database.GetCollection<BsonDocument>(FilePartsCollection)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
    }

    private static Task<BsonDocument?> FindEncryptedFileAsync(IMongoDatabase database, long id)
    {
        return database.GetCollection<BsonDocument>(EncryptedFilesCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstOrDefaultAsync()!;
    }

    /// <summary>Deterministic, per-case distinct part payloads of the generated lengths.</summary>
    private static byte[][] BuildParts(IReadOnlyList<int> sizes, int seed)
    {
        var parts = new byte[sizes.Count][];
        for (var i = 0; i < sizes.Count; i++)
        {
            var bytes = new byte[sizes[i]];
            for (var j = 0; j < bytes.Length; j++)
            {
                bytes[j] = unchecked((byte)(seed * 7 + i * 31 + j * 13 + 1));
            }

            parts[i] = bytes;
        }

        return parts;
    }

    private static byte[] Concat(IReadOnlyList<byte[]> parts)
    {
        var blob = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, blob, offset, part.Length);
            offset += part.Length;
        }

        return blob;
    }

    /// <summary>Flips the first hex digit of a checksum, producing a valid-shaped but wrong value.</summary>
    private static string Corrupt(string md5)
    {
        var chars = md5.ToCharArray();
        chars[0] = chars[0] == '0' ? '1' : '0';

        return new string(chars);
    }

    private static IOptionsMonitor<MyTelegramMessengerServerOptions> TestOptions(int thisDcId)
    {
        return new FixedOptionsMonitor(new MyTelegramMessengerServerOptions { ThisDcId = thisDcId });
    }

    private sealed class FixedOptionsMonitor(MyTelegramMessengerServerOptions value)
        : IOptionsMonitor<MyTelegramMessengerServerOptions>
    {
        public MyTelegramMessengerServerOptions CurrentValue { get; } = value;
        public MyTelegramMessengerServerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MyTelegramMessengerServerOptions, string?> listener) => null;
    }

    private static IReadOnlyList<T> Sample<T>(Gen<T> generator, int count)
    {
        return Gen.Sample(SampleSize, count, generator).ToList();
    }
}

/// <summary>How a reference to a stored encrypted file is (or is not) corrupted.</summary>
public enum EncryptedFileCorruption
{
    /// <summary>A faithful reference to the stored file — it must resolve and be reused.</summary>
    None,

    WrongAccessHash,
    WrongId,
    WrongIdAndAccessHash,

    /// <summary><c>inputEncryptedFileEmpty</c>: references no stored file at all.</summary>
    EmptyInput
}

/// <summary>The parts saved for one encrypted file, plus the client-supplied key fingerprint.</summary>
public sealed record EncryptedFileUploadCase(int[] PartSizes, int KeyFingerprint)
{
    public override string ToString() =>
        $"Upload(parts=[{string.Join(",", PartSizes)}], keyFingerprint={KeyFingerprint})";
}

/// <summary>Saved parts plus a declared <c>parts</c> count that deliberately differs from them.</summary>
public sealed record EncryptedFilePartsMismatchCase(int[] PartSizes, int DeclaredParts, int KeyFingerprint)
{
    public override string ToString() =>
        $"PartsMismatch(saved=[{string.Join(",", PartSizes)}], declared={DeclaredParts}, " +
        $"keyFingerprint={KeyFingerprint})";
}

/// <summary>
/// Saved parts whose <c>FilePart</c> indices skip <see cref="MissingIndex"/>, so the run has a hole even
/// though the number of saved parts matches the declared count.
/// </summary>
public sealed record EncryptedFileGapCase(int[] PartSizes, int MissingIndex, int KeyFingerprint)
{
    public override string ToString() =>
        $"Gap(parts=[{string.Join(",", PartSizes)}], missingIndex={MissingIndex}, " +
        $"keyFingerprint={KeyFingerprint})";
}

/// <summary>An uploaded file plus the way its reference is corrupted for the failure half of Property 14.</summary>
public sealed record EncryptedFileReferenceCase(int[] PartSizes, int KeyFingerprint,
    EncryptedFileCorruption Corruption)
{
    public override string ToString() =>
        $"Reference(parts=[{string.Join(",", PartSizes)}], keyFingerprint={KeyFingerprint}, " +
        $"corruption={Corruption})";
}

/// <summary>A full reuse scenario for the in-memory property: who sends, what is referenced, how.</summary>
public sealed record EncryptedFileReuseCase(int[] PartSizes,
    int KeyFingerprint,
    EncryptedFileCorruption Corruption,
    bool CallerIsAdmin,
    long SendRandomId,
    bool Silent)
{
    public override string ToString() =>
        $"Reuse(parts=[{string.Join(",", PartSizes)}], keyFingerprint={KeyFingerprint}, " +
        $"corruption={Corruption}, callerIsAdmin={CallerIsAdmin}, randomId={SendRandomId}, silent={Silent})";
}

/// <summary>Generators for the encrypted-file properties.</summary>
public static class EncryptedFileGen
{
    /// <summary>1..6 parts of 1..96 bytes: the total blob length differs from case to case.</summary>
    public static Gen<int[]> PartSizes =>
        from count in Gen.Choose(1, 6)
        from sizes in Gen.ArrayOf(count, Gen.Choose(1, 96))
        select sizes;

    /// <summary>Arbitrary 32-bit fingerprints, including the extremes and negatives.</summary>
    public static Gen<int> KeyFingerprint =>
        Gen.Frequency(Tuple.Create(4, Gen.Choose(-1_000_000_000, 1_000_000_000)),
            Tuple.Create(1, Gen.Elements(int.MinValue, -1, 0, 1, int.MaxValue)));

    /// <summary>Declared part counts an empty (never-uploaded) file might carry, including 0.</summary>
    public static Gen<int> DeclaredPartsForEmptyFile => Gen.Choose(0, 8);

    public static Gen<EncryptedFileCorruption> Corruption =>
        Gen.Elements(EncryptedFileCorruption.WrongAccessHash,
            EncryptedFileCorruption.WrongId,
            EncryptedFileCorruption.WrongIdAndAccessHash,
            EncryptedFileCorruption.EmptyInput);

    /// <summary>Includes the faithful reference, so the reuse half of Property 14 is exercised too.</summary>
    public static Gen<EncryptedFileCorruption> CorruptionOrNone =>
        Gen.Frequency(Tuple.Create(2, Gen.Constant(EncryptedFileCorruption.None)),
            Tuple.Create(3, Corruption));

    public static Gen<EncryptedFileUploadCase> UploadCase =>
        from sizes in PartSizes
        from fingerprint in KeyFingerprint
        select new EncryptedFileUploadCase(sizes, fingerprint);

    /// <summary>
    /// The declared count is drawn from 1..11 and shifted past the actual count, so it is always positive
    /// and never accidentally equal to the number of saved parts.
    /// </summary>
    public static Gen<EncryptedFilePartsMismatchCase> PartsMismatchCase =>
        from sizes in PartSizes
        from declaredRaw in Gen.Choose(1, 11)
        from fingerprint in KeyFingerprint
        select new EncryptedFilePartsMismatchCase(sizes,
            declaredRaw >= sizes.Length ? declaredRaw + 1 : declaredRaw,
            fingerprint);

    /// <summary>
    /// <c>n</c> saved parts drawn from the indices 0..n with exactly one of 0..n-1 skipped, so the run
    /// always has a hole (a skipped 0 shifts the whole run, a skipped inner index splits it) while the
    /// number of parts still equals the declared count.
    /// </summary>
    public static Gen<EncryptedFileGapCase> GapCase =>
        from count in Gen.Choose(1, 5)
        from sizes in Gen.ArrayOf(count, Gen.Choose(1, 64))
        from missing in Gen.Choose(0, count - 1)
        from fingerprint in KeyFingerprint
        select new EncryptedFileGapCase(sizes, missing, fingerprint);

    public static Gen<EncryptedFileReferenceCase> ReferenceCase =>
        from sizes in PartSizes
        from fingerprint in KeyFingerprint
        from corruption in Corruption
        select new EncryptedFileReferenceCase(sizes, fingerprint, corruption);

    public static Gen<EncryptedFileReuseCase> ReuseCase =>
        from sizes in PartSizes
        from fingerprint in KeyFingerprint
        from corruption in CorruptionOrNone
        from callerIsAdmin in Gen.Elements(true, false)
        from randomId in Gen.Choose(1, 1_000_000)
        from silent in Gen.Elements(true, false)
        select new EncryptedFileReuseCase(sizes, fingerprint, corruption, callerIsAdmin, randomId, silent);
}

/// <summary>
/// FsCheck arbitrary registration surface for the encrypted-file properties. The Mongo-backed facts cannot
/// carry <c>[Property]</c> (they need a real MongoDB via <c>[RequiresMongoDbFact]</c>), so they sample these
/// arbitraries' generators directly; the in-memory property registers this class as its arbitrary source.
/// </summary>
public static class EncryptedFileArbitraries
{
    public static Arbitrary<EncryptedFileUploadCase> UploadCase() => Arb.From(EncryptedFileGen.UploadCase);

    public static Arbitrary<EncryptedFilePartsMismatchCase> PartsMismatchCase() =>
        Arb.From(EncryptedFileGen.PartsMismatchCase);

    public static Arbitrary<EncryptedFileGapCase> GapCase() => Arb.From(EncryptedFileGen.GapCase);

    public static Arbitrary<EncryptedFileReferenceCase> ReferenceCase() =>
        Arb.From(EncryptedFileGen.ReferenceCase);

    public static Arbitrary<EncryptedFileReuseCase> ReuseCase() => Arb.From(EncryptedFileGen.ReuseCase);
}
