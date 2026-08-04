namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Fetch the full list of usable <a href="https://corefork.telegram.org/api/effects">animated message effects »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getAvailableEffects"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAvailableEffectsHandler(
    IMessageEffectAppService messageEffectAppService,
    IAccessHashHelper2 accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAvailableEffects,
        MyTelegram.Schema.Messages.IAvailableEffects>
{
    protected override async Task<MyTelegram.Schema.Messages.IAvailableEffects> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetAvailableEffects obj)
    {
        var effects = await messageEffectAppService.GetAllAsync();
        var hash = messageEffectAppService.GetHash(effects);

        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TAvailableEffectsNotModified();
        }

        // Every id on availableEffect points into the documents vector of this same response, and
        // several effects may share a document, so documents are collected without duplicates.
        var documents = new Dictionary<long, IDocument>();
        var availableEffects = new TVector<IAvailableEffect>();

        foreach (var effect in effects)
        {
            AddDocument(input, documents, effect.StaticIcon);
            AddDocument(input, documents, effect.EffectSticker);
            AddDocument(input, documents, effect.EffectAnimation);

            availableEffects.Add(new TAvailableEffect
            {
                Id = effect.EffectId,
                Emoticon = effect.Emoticon,
                PremiumRequired = effect.PremiumRequired,
                StaticIconId = effect.StaticIcon?.DocumentId,
                EffectStickerId = effect.EffectSticker.DocumentId,
                EffectAnimationId = effect.EffectAnimation?.DocumentId
            });
        }

        return new TAvailableEffects
        {
            Hash = hash,
            Effects = availableEffects,
            Documents = new TVector<IDocument>(documents.Values)
        };
    }

    private void AddDocument(
        IRequestInput input,
        Dictionary<long, IDocument> documents,
        MessageEffectDocument? source)
    {
        if (source == null || documents.ContainsKey(source.DocumentId))
        {
            return;
        }

        documents.Add(source.DocumentId, new TDocument
        {
            Id = source.DocumentId,
            AccessHash = accessHashHelper.GenerateAccessHash(
                input.UserId, input.AccessHashKeyId, source.DocumentId, AccessHashType.Document),
            FileReference = source.FileReference,
            Date = source.Date,
            MimeType = source.MimeType,
            Size = source.Size,
            DcId = source.DcId,
            Thumbs = source.Thumbs,
            VideoThumbs = new TVector<IVideoSize>(),
            Attributes = BuildAttributes(source.MimeType)
        });
    }

    private static TVector<IDocumentAttribute> BuildAttributes(string mimeType)
    {
        if (mimeType == "application/x-tgsticker")
        {
            return
            [
                new TDocumentAttributeImageSize { W = 512, H = 512 },
                new TDocumentAttributeFilename { FileName = "AnimatedSticker.tgs" }
            ];
        }

        return [];
    }
}
