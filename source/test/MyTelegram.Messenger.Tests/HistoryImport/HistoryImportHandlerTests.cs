using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.VideoProcessing;
using MyTelegram.Messenger.Tests.AccountDeletion;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages — the four steps of the flow after the export file has been validated.
///
/// <para>
/// The client uploads the export file with <c>upload.saveFilePart</c> and hands the resulting
/// <c>InputFile</c> to <c>messages.initHistoryImport</c>; the server reads the parts back, parses the
/// file and answers an <c>import_id</c>, which then has to be presented by every following call of the
/// same user for the same chat.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class HistoryImportHandlerTests
{
    private const long UserId = 2010001;
    private const long ChannelId = 1000001;
    private const long FileId = 5555;

    private const string Export = """
        12/31/20, 11:59 PM - John Doe: Happy new year!
        1/1/21, 00:05 AM - Jane: IMG-0001.jpg (file attached)
        """;

    [Fact]
    public async Task An_unrecognized_file_is_IMPORT_FORMAT_UNRECOGNIZED()
    {
        var handler = CreateCheckHandler();

        var exception = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(handler,
            new RequestCheckHistoryImport { ImportHead = "Dear diary" }, UserId));

        exception.RpcError.Message.ShouldBe("IMPORT_FORMAT_UNRECOGNIZED");
    }

    [Fact]
    public async Task An_empty_head_is_IMPORT_FILE_INVALID()
    {
        var handler = CreateCheckHandler();

        var exception = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(handler,
            new RequestCheckHistoryImport { ImportHead = "   " }, UserId));

        exception.RpcError.Message.ShouldBe("IMPORT_FILE_INVALID");
    }

    [Fact]
    public async Task A_WhatsApp_head_is_reported_as_a_private_chat()
    {
        var handler = CreateCheckHandler();

        var result = await HandlerInvoker.InvokeAsync(handler,
            new RequestCheckHistoryImport { ImportHead = Export }, UserId);

        var parsed = result.ShouldBeOfType<THistoryImportParsed>();
        parsed.Pm.ShouldBeTrue();
        parsed.Group.ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task An_uploaded_export_file_is_parsed_and_answered_with_an_import_id()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, Encoding.UTF8.GetBytes(Export));
        var store = CreateStore(mongo);

        var result = await HandlerInvoker.InvokeAsync(CreateInitHandler(mongo.Database, store), InitRequest(),
            UserId);

        var import = result.ShouldBeOfType<Schema.Messages.THistoryImport>();
        import.Id.ShouldBeGreaterThan(0);

        var document = await store.GetAsync(import.Id);
        document!.UserId.ShouldBe(UserId);
        document.TotalMessages.ShouldBe(2);
        document.Status.ShouldBe(HistoryImportStatus.Pending);
    }

    [RequiresMongoDbFact]
    public async Task The_messages_are_stored_in_chronological_order()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // The clients render a history by message id, and the ids follow the order the worker sends
        // the messages in, so an export whose lines are out of order has to be sorted first.
        await SaveUploadAsync(mongo.Database, Encoding.UTF8.GetBytes("""
            2/1/21, 10:00 AM - John Doe: second
            1/1/21, 09:00 AM - John Doe: first
            """));
        var store = CreateStore(mongo);

        var result = await HandlerInvoker.InvokeAsync(CreateInitHandler(mongo.Database, store), InitRequest(),
            UserId);

        var importId = result.ShouldBeOfType<Schema.Messages.THistoryImport>().Id;
        var messages = await store.ReadMessagesAsync(importId, 0, 10);

        messages.Select(p => p.Text).ShouldBe(["first", "second"]);
        messages.Select(p => p.Seq).ShouldBe([0, 1]);
        messages[0].Date.ShouldBeLessThan(messages[1].Date);
    }

    [RequiresMongoDbFact]
    public async Task The_consumed_upload_parts_are_dropped()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, Encoding.UTF8.GetBytes(Export));

        await HandlerInvoker.InvokeAsync(CreateInitHandler(mongo.Database, CreateStore(mongo)), InitRequest(),
            UserId);

        var remaining = await mongo.Database.GetCollection<BsonDocument>("file_parts")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("FileId", FileId));
        remaining.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task An_export_file_that_was_never_uploaded_is_IMPORT_FILE_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var exception = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(
            CreateInitHandler(mongo.Database, CreateStore(mongo)), InitRequest(), UserId));

        exception.RpcError.Message.ShouldBe("IMPORT_FILE_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_file_above_the_size_cap_is_IMPORT_FILE_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SaveUploadAsync(mongo.Database, Encoding.UTF8.GetBytes(Export));

        var handler = CreateInitHandler(mongo.Database, CreateStore(mongo), maxFileSizeBytes: 8);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            HandlerInvoker.InvokeAsync(handler, InitRequest(), UserId));

        exception.RpcError.Message.ShouldBe("IMPORT_FILE_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_second_import_into_the_same_chat_has_to_wait()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        await store.CreateAsync(UserId, new Peer(PeerType.Channel, ChannelId), ChatExportFormat.WhatsApp, 0, 222,
            [new ImportedMessageLine(1609459140, "John", "hi", null)]);
        await SaveUploadAsync(mongo.Database, Encoding.UTF8.GetBytes(Export));

        var exception = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(
            CreateInitHandler(mongo.Database, store), InitRequest(), UserId));

        exception.RpcError.ErrorCode.ShouldBe(406);
        exception.RpcError.Message.ShouldBe("PREVIOUS_CHAT_IMPORT_ACTIVE_WAIT_30MIN");
    }

    [RequiresMongoDbFact]
    public async Task An_uploaded_media_file_is_kept_under_its_name()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(UserId, new Peer(PeerType.Channel, ChannelId),
            ChatExportFormat.WhatsApp, 1, 222, [new ImportedMessageLine(1609459140, "John", "hi", null)]);

        var result = await HandlerInvoker.InvokeAsync(CreateUploadHandler(store),
            new RequestUploadImportedMedia
            {
                Peer = InputChannel(),
                ImportId = import.Id,
                FileName = "IMG-0001.jpg",
                Media = new TInputMediaEmpty()
            }, UserId);

        result.ShouldBeOfType<TMessageMediaEmpty>();
        (await store.GetMediaAsync(import.Id, ["IMG-0001.jpg"])).Count.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task An_import_of_another_user_cannot_be_fed_or_started()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(9999, new Peer(PeerType.Channel, ChannelId),
            ChatExportFormat.WhatsApp, 0, 222, [new ImportedMessageLine(1609459140, "John", "hi", null)]);

        var upload = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(
            CreateUploadHandler(store),
            new RequestUploadImportedMedia
            {
                Peer = InputChannel(),
                ImportId = import.Id,
                FileName = "IMG-0001.jpg",
                Media = new TInputMediaEmpty()
            }, UserId));
        upload.RpcError.Message.ShouldBe("IMPORT_ID_INVALID");

        var start = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(
            CreateStartHandler(store),
            new RequestStartHistoryImport { Peer = InputChannel(), ImportId = import.Id }, UserId));
        start.RpcError.Message.ShouldBe("IMPORT_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task Starting_an_import_queues_it_for_the_worker()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = CreateStore(mongo);
        var import = await store.CreateAsync(UserId, new Peer(PeerType.Channel, ChannelId),
            ChatExportFormat.WhatsApp, 0, 222, [new ImportedMessageLine(1609459140, "John", "hi", null)]);

        var result = await HandlerInvoker.InvokeAsync(CreateStartHandler(store),
            new RequestStartHistoryImport { Peer = InputChannel(), ImportId = import.Id }, UserId);

        result.ShouldBeOfType<TBoolTrue>();
        (await store.GetAsync(import.Id))!.Status.ShouldBe(HistoryImportStatus.Queued);

        // The messages are injected by the worker, so starting twice must not be possible.
        var second = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(
            CreateStartHandler(store),
            new RequestStartHistoryImport { Peer = InputChannel(), ImportId = import.Id }, UserId));
        second.RpcError.Message.ShouldBe("IMPORT_ID_INVALID");
    }

    private static RequestInitHistoryImport InitRequest()
    {
        return new RequestInitHistoryImport
        {
            Peer = InputChannel(),
            File = new TInputFile { Id = FileId, Parts = 1, Name = "chat.txt", Md5Checksum = string.Empty },
            MediaCount = 1
        };
    }

    private static IInputPeer InputChannel() => new TInputPeerChannel { ChannelId = ChannelId, AccessHash = 0 };

    private static HistoryImportStore CreateStore(EmbeddedMongoServer mongo)
    {
        return new HistoryImportStore(mongo.Database, NullLogger<HistoryImportStore>.Instance);
    }

    private static Task SaveUploadAsync(IMongoDatabase database, byte[] bytes)
    {
        return database.GetCollection<BsonDocument>("file_parts").InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"{UserId}_{FileId}_0",
            ["UserId"] = UserId,
            ["FileId"] = FileId,
            ["FilePart"] = 0,
            ["Bytes"] = bytes,
            ["Size"] = bytes.Length
        });
    }

    private static object CreateCheckHandler()
    {
        return Activate("CheckHistoryImportHandler", new ChatExportParser());
    }

    private static object CreateInitHandler(IMongoDatabase database, IHistoryImportStore store,
        long maxFileSizeBytes = 32 * 1024 * 1024)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MyTelegramMessengerServerOptions
        {
            HistoryImport = new HistoryImportConfig { MaxFileSizeBytes = maxFileSizeBytes }
        });

        return Activate("InitHistoryImportHandler", PeerHelper(), Validator(), new ChatExportParser(), store,
            FileReader(database), database, options, NullLogger<object>.Instance);
    }

    /// <summary>
    /// The real reader over a file server that answers nothing, so only the staged parts in MongoDB
    /// can satisfy the read. The file server path has its own test.
    /// </summary>
    private static IHistoryImportFileReader FileReader(IMongoDatabase database)
    {
        var mediaHelper = new Mock<IMediaHelper>(MockBehavior.Loose);
        mediaHelper.Setup(p => p.SaveMediaAsync(It.IsAny<IInputMedia>())).ReturnsAsync((IMessageMedia?)null);

        return new HistoryImportFileReader(database, mediaHelper.Object,
            new Mock<IStoredFileStorage>(MockBehavior.Loose).Object,
            NullLogger<HistoryImportFileReader>.Instance);
    }

    private static object CreateUploadHandler(IHistoryImportStore store)
    {
        var mediaHelper = new Mock<IMediaHelper>(MockBehavior.Loose);
        mediaHelper.Setup(p => p.SaveMediaAsync(It.IsAny<IInputMedia>()))
            .ReturnsAsync(new TMessageMediaEmpty());

        return Activate("UploadImportedMediaHandler", PeerHelper(), Validator(), store, mediaHelper.Object);
    }

    private static object CreateStartHandler(IHistoryImportStore store)
    {
        return Activate("StartHistoryImportHandler", PeerHelper(), Validator(), store,
            NullLogger<object>.Instance);
    }

    private static IPeerHelper PeerHelper()
    {
        var peerHelper = new Mock<IPeerHelper>(MockBehavior.Loose);
        peerHelper.Setup(p => p.GetPeer(It.IsAny<IInputPeer>(), It.IsAny<long>()))
            .Returns(new Peer(PeerType.Channel, ChannelId));

        return peerHelper.Object;
    }

    /// <summary>The peer rules have their own tests; here every destination is allowed.</summary>
    private static IHistoryImportPeerValidator Validator()
    {
        var validator = new Mock<IHistoryImportPeerValidator>(MockBehavior.Loose);
        validator.Setup(p => p.ValidateAsync(It.IsAny<long>(), It.IsAny<Peer>(), It.IsAny<bool>()))
            .ReturnsAsync("Family");

        return validator.Object;
    }

    /// <summary>The handlers are internal to the messenger assembly and have no public constructor.</summary>
    private static object Activate(string typeName, params object[] arguments)
    {
        var type = typeof(ChatExportParser).Assembly.GetType(
            $"MyTelegram.Messenger.Handlers.LatestLayer.Messages.{typeName}", throwOnError: true)!;

        var constructor = type.GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var resolved = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            // The loggers are typed on the handler itself, which the caller cannot name.
            resolved[i] = parameters[i].ParameterType.IsGenericType &&
                          parameters[i].ParameterType.GetGenericTypeDefinition() == typeof(ILogger<>)
                ? Activator.CreateInstance(typeof(NullLogger<>)
                    .MakeGenericType(parameters[i].ParameterType.GetGenericArguments()[0]))!
                : arguments[i];
        }

        return constructor.Invoke(resolved);
    }
}
