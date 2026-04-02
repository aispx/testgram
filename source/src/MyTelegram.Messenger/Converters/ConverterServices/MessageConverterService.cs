using System.Buffers.Binary;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Converters.ConverterServices;

public class MessageConverterService(
    IMessageMediaResponseService messageMediaResponseService,
    ILayeredService<IMessageConverter> messageLayeredService,
    ILayeredService<IMessageServiceConverter> messageServiceLayeredService,
    ILayeredService<IMessageFwdHeaderConverter> messageFwdHeaderLayeredService,
    ILayeredService<IPollConverter> pollLayeredService,
    IDataEncryptionHelper dataEncryptionHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IMongoDatabase mongoDatabase,
    ILogger<MessageConverterService> logger
) : IMessageConverterService, ITransientDependency
{
    private static Dictionary<int, byte[]> _keys = [];
    public IMessage ToMessage(
        long selfUserId,
        IMessageReadModel readModel,
        IPollReadModel? pollReadModel = null,
        List<string>? chosenOptions = null,
        List<IUserReactionReadModel>? userReactionReadModels = null,
        int layer = 0
    )
    {
        IMessageMedia? media = null;
        if (pollReadModel != null)
        {
            media = new TMessageMediaPoll
            {
                Poll = pollLayeredService.GetConverter(layer).ToPoll(pollReadModel),
                Results = pollLayeredService
                    .GetConverter(layer)
                    .ToPollResults(pollReadModel, chosenOptions),
            };
        }

        return ToMessage(selfUserId, readModel, media, userReactionReadModels, layer);
    }

    private IMessage ToMessage(
        long selfUserId,
        IMessageReadModel readModel,
        IMessageMedia? pollMedia = null,
        IReadOnlyCollection<IUserReactionReadModel>? userReactionReadModels = null,
        int layer = 0
    )
    {
        var fromId = new Peer(PeerType.User, readModel.SenderPeerId).ToPeer();
        switch (readModel.SendMessageType)
        {
            case SendMessageType.MessageService:
                {
                    var m = messageServiceLayeredService.GetConverter(layer).ToMessage(readModel);
                    if (readModel.ToPeerType == PeerType.Channel)
                    {
                        if (
                            readModel.Post
                            || readModel.MessageActionType == MessageActionType.ChannelCreate
                            || readModel.MessageActionType == MessageActionType.SetMessagesTtl
                            || readModel.MessageActionType == MessageActionType.PaidMessagesPriceUpdated
                        )
                        {
                            fromId = null;
                        }
                    }
                    if (readModel.SendAs != null)
                    {
                        fromId = readModel.SendAs.ToPeer();
                    }

                    m.FromId = fromId;
                    m.Out = readModel.SenderPeerId == selfUserId;

                    if (readModel.MentionedUserIds?.Contains(selfUserId) ?? false)
                    {
                        m.Mentioned = true;
                        m.MediaUnread = true;
                    }

                    return m;
                }

            default:
                {
                    var m = messageLayeredService.GetConverter(layer).ToMessage(readModel);
                    logger.LogInformation("[DEBUG] ToMessage: MessageId={MessageId}, Media2={Media2Type}, Media length={MediaLength}",
                        readModel.MessageId, readModel.Media2?.GetType().Name ?? "null", readModel.Media?.Length ?? 0);

                    var media = readModel.Media2 ?? readModel.Media.ToTObject<IMessageMedia>();
                    logger.LogInformation("[DEBUG] ToMessage: After deserialization, media type={MediaType}", media?.GetType().Name ?? "null");

                    // If Media2 is null, try to reconstruct from DocumentId
                    if (media == null && readModel.DocumentId.HasValue && readModel.DocumentId.Value > 0)
                    {
                        logger.LogInformation("[DEBUG] Attempting to reconstruct media from DocumentId={DocumentId} for message {MessageId}",
                            readModel.DocumentId, readModel.MessageId);
                        // TODO: Implement document reconstruction
                    }

                    // Enrich documents in media with Attributes2 from database
                    logger.LogInformation("[DEBUG] ToMessage: About to call EnrichMediaDocuments");
                    media = EnrichMediaDocuments(media);
                    logger.LogInformation("[DEBUG] ToMessage: After EnrichMediaDocuments");

                    m.Media = messageMediaResponseService.ToLayeredData(media, layer);
                    m.Out = readModel.SenderPeerId == selfUserId;
                    m.FromId = fromId;
                    if (readModel.FwdHeader != null)
                    {
                        m.FwdFrom = messageFwdHeaderLayeredService
                            .GetConverter(layer)
                            .ToMessageFwdHeader(readModel.FwdHeader);
                    }

                    if (readModel.ToPeerType == PeerType.Channel)
                    {
                        m.Replies = ToMessageReplies(readModel.Post, readModel.Reply);
                        if (m.Replies != null && readModel.FwdHeader != null) // forward from linked channel
                        {
                            m.FromId = readModel.FwdHeader.FromId.ToPeer();
                            m.Out = false;
                        }

                        if (readModel.Post)
                        {
                            m.FromId = null;
                        }

                        if (readModel.SendAs != null)
                        {
                            m.FromId = readModel.SendAs.ToPeer();
                        }
                    }

                    if (m.QuickReplyShortcutId.HasValue)
                    {
                        m.Date = 0;
                    }

                    if (m.Out)
                    {
                        m.FromScheduled = readModel.FromScheduled;
                    }

                    if (pollMedia != null)
                    {
                        m.Media = pollMedia;
                    }

                    if (readModel.MentionedUserIds?.Contains(selfUserId) ?? false)
                    {
                        m.Mentioned = true;
                        m.MediaUnread = true;
                    }

                    if (readModel.EncryptedData?.Length > 0)
                    {
                        m.Message = DecryptMessage(readModel.OwnerPeerId, readModel.MessageId, readModel.EncryptedData);
                    }

                    if (string.IsNullOrEmpty(m.Message))
                    {
                        m.Message = string.Empty;
                    }

                    // Set ChosenOrder for current user's reactions
                    if (m.Reactions is TMessageReactions mr && mr.Results?.Count > 0 && readModel.RecentReactions2 != null)
                    {
                        var myReactionIds = readModel.RecentReactions2
                            .Where(r => r.SenderUserId == selfUserId)
                            .Select((r, i) => (Id: r.Reaction.GetReactionId(), Order: i))
                            .GroupBy(x => x.Id)
                            .ToDictionary(g => g.Key, g => g.First().Order);
                        foreach (var rc in mr.Results.OfType<TReactionCount>())
                        {
                            if (myReactionIds.TryGetValue(rc.Reaction.GetReactionId(), out var order))
                                rc.ChosenOrder = order;
                        }
                        // Mark my reactions in recent_reactions
                        if (mr.RecentReactions != null)
                        {
                            foreach (var rr in mr.RecentReactions.OfType<TMessagePeerReaction>())
                            {
                                if (rr.PeerId is TPeerUser pu && pu.UserId == selfUserId)
                                    rr.My = true;
                            }
                        }
                    }

                    return m;
                }
        }
    }

    public List<IMessage> ToMessageList(
        long selfUserId,
        IReadOnlyCollection<IMessageReadModel> messageReadModels,
        IReadOnlyCollection<IPollReadModel>? pollReadModels,
        IReadOnlyCollection<IPollAnswerVoterReadModel>? pollAnswerVoterReadModels,
        IReadOnlyCollection<IUserReactionReadModel>? userReactionReadModels,
        int layer = 0
    )
    {
        var messages = new List<IMessage>();
        var pollReadModelsDict = pollReadModels?.ToDictionary(p => p.PollId) ?? [];
        var pollAnswerVoterReadModelsDict =
            pollAnswerVoterReadModels
                ?.GroupBy(p => p.PollId)
                .ToDictionary(k => k.Key, v => v.Select(x => x.Option).ToList()) ?? [];

        var pollConverter = pollLayeredService.GetConverter(layer);

        foreach (var readModel in messageReadModels)
        {
            IMessageMedia? media = null;
            if (readModel.PollId.HasValue)
            {
                pollReadModelsDict.TryGetValue(readModel.PollId.Value, out var poll);
                pollAnswerVoterReadModelsDict.TryGetValue(
                    readModel.PollId.Value,
                    out var chosenOptions
                );

                if (poll != null)
                {
                    media = new TMessageMediaPoll
                    {
                        Poll = pollConverter.ToPoll(poll),
                        Results = pollConverter.ToPollResults(poll, chosenOptions),
                    };
                }
            }

            messages.Add(ToMessage(selfUserId, readModel, media, null, layer));
        }

        return messages;
    }

    public IMessage ToMessage(
        long selfUserId,
        MessageItem item,
        List<long>? userReactionIds = null,
        bool mentioned = false,
        int layer = 0
    )
    {
        var isOut = item.IsOut;
        var fromId = item.SenderPeer.ToPeer();

        if (item.ToPeer.PeerType == PeerType.Channel /*&& selfUserId != 0*/)
        {
            isOut = item.SenderPeer.PeerId == selfUserId;
        }

        switch (item.SendMessageType)
        {
            case SendMessageType.MessageService:
                {
                    if (item.ToPeer.PeerType == PeerType.Channel)
                    {
                        if (
                            item.Post
                            || item.MessageSubType == MessageSubType.CreateChannel
                            || item.MessageSubType == MessageSubType.SetHistoryTtl
                        )
                        {
                            fromId = null;
                        }
                    }

                    var m = messageServiceLayeredService.GetConverter(layer).ToMessage(item);

                    m.Out = isOut;
                    m.Mentioned = mentioned;
                    m.MediaUnread = mentioned;
                    m.FromId = fromId;
                    if (item.SendAs != null)
                    {
                        m.FromId = item.SendAs.ToPeer();
                    }

                    return m;
                }

            default:
                {
                    // Enrich documents in media with Attributes2 from database
                    var enrichedMedia = EnrichMediaDocuments(item.Media);
                    var media = messageMediaResponseService.ToLayeredData(enrichedMedia, layer);
                    var m = messageLayeredService.GetConverter(layer).ToMessage(item);

                    m.Media = media;
                    m.Out = isOut;
                    m.Mentioned = mentioned;
                    m.MediaUnread = mentioned;
                    m.FromId = fromId;

                    if (item.FwdHeader != null)
                    {
                        m.FwdFrom = messageFwdHeaderLayeredService
                            .GetConverter(layer)
                            .ToMessageFwdHeader(item.FwdHeader);
                    }

                    if (item.Post)
                    {
                        m.FromId = null;
                    }

                    if (item.ToPeer.PeerType == PeerType.Channel)
                    {
                        m.Replies = ToMessageReplies(item.Post, item.Reply);
                        if (m.Replies != null && item.FwdHeader?.SavedFromPeer != null) // forward from linked channel
                        {
                            //m.FromId = _peerHelper.ToPeer(PeerType.Channel, item.FwdHeader.SavedFromPeer.PeerId);
                            m.FromId = item.FwdHeader.SavedFromPeer.ToPeer();
                            m.Out = false;
                        }

                        if (item.Post)
                        {
                            m.FromId = null;
                        }

                        if (item.SendAs != null)
                        {
                            m.FromId = item.SendAs.ToPeer();
                        }
                    }


                    if (m.QuickReplyShortcutId.HasValue)
                    {
                        m.Date = 0;
                    }

                    if (m.Out)
                    {
                        m.FromScheduled = item.ScheduleDate.HasValue;
                    }

                    if (item.EncryptedData?.Length > 0)
                    {
                        m.Message = DecryptMessage(item.OwnerPeer.PeerId, item.MessageId, item.EncryptedData);
                    }

                    return m;
                }
        }
    }

    protected IMessageReplies? ToMessageReplies(bool post, MessageReply? reply)
    {
        if (reply == null)
        {
            return null;
        }

        if (post)
        {
            if (reply.ChannelId == null)
            {
                return null;
            }
        }

        var messageReplies = new TMessageReplies
        {
            Comments = post,
            MaxId = reply.MaxId,
            Replies = reply.Replies,
            RepliesPts = reply.RepliesPts,
        };

        if (post)
        {
            messageReplies.ChannelId = reply.ChannelId;
            messageReplies.RecentRepliers = [];
            if (reply.RecentRepliers?.Count > 0)
            {
                messageReplies.RecentRepliers = [.. reply.RecentRepliers.Select(p => p.ToPeer())];
            }
        }

        return messageReplies;
    }

    private void InitKeys()
    {
        if (_keys.Count == 0)
        {
            _keys = options.CurrentValue.EncryptionConfig.MessageKeys.ToDictionary(k => k.Id,
                v => Encoding.UTF8.GetBytes(v.Key));
        }
    }

    public string DecryptMessage(long ownerPeerId, int messageId, ReadOnlyMemory<byte>? encryptedData)
    {
        if (encryptedData == null || encryptedData.Value.IsEmpty)
        {
            return string.Empty;
        }
        //var key=op

        InitKeys();

        var encryptedSpan = encryptedData.Value.Span;
        var keyId = BinaryPrimitives.ReadInt32LittleEndian(encryptedSpan);

        if (!_keys.TryGetValue(keyId, out var masterKey))
        {
            return string.Empty;
        }
        var tempBytes = ArrayPool<byte>.Shared.Rent(encryptedSpan.Length);
        try
        {
            var span = tempBytes.AsSpan(0, encryptedSpan.Length);
            var count = dataEncryptionHelper.Decrypt(masterKey, ownerPeerId, encryptedSpan.Slice(4), span);
            return Encoding.UTF8.GetString(span.Slice(0, count));
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            Console.WriteLine($"[DecryptMessage] Failed to decrypt ownerPeerId={ownerPeerId} messageId={messageId}");
            return string.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempBytes);
        }
    }

    private IMessageMedia? EnrichMediaDocuments(IMessageMedia? media)
    {
        if (media is not TMessageMediaDocument mediaDoc || mediaDoc.Document is not TDocument doc)
        {
            return media;
        }

        try
        {
            // Load document from MongoDB to get Attributes2 with stickerset info
            var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
            var filter = Builders<BsonDocument>.Filter.Eq("DocumentId", doc.Id);
            var docBson = docCol.Find(filter).FirstOrDefault();

            if (docBson != null && docBson.Contains("Attributes2") && docBson["Attributes2"] != BsonNull.Value)
            {
                // Deserialize Attributes2 - handle both short and full type names
                var attributes2Array = docBson["Attributes2"].AsBsonArray;
                var attributes2 = new List<IDocumentAttribute>();

                foreach (var attrBson in attributes2Array)
                {
                    var attrDoc = attrBson.AsBsonDocument;
                    var typeName = attrDoc["_t"].AsString;

                    // Handle both "TDocumentAttributeSticker" and "MyTelegram.Schema.TDocumentAttributeSticker"
                    if (typeName.EndsWith("TDocumentAttributeSticker"))
                    {
                        var stickerAttr = new TDocumentAttributeSticker
                        {
                            Alt = attrDoc.Contains("Alt") ? attrDoc["Alt"].AsString : "",
                            Mask = attrDoc.Contains("Mask") && attrDoc["Mask"].AsBoolean
                        };

                        if (attrDoc.Contains("Stickerset") && attrDoc["Stickerset"] != BsonNull.Value)
                        {
                            var stickersetDoc = attrDoc["Stickerset"].AsBsonDocument;
                            var stickersetType = stickersetDoc["_t"].AsString;

                            if (stickersetType.EndsWith("TInputStickerSetID"))
                            {
                                stickerAttr.Stickerset = new TInputStickerSetID
                                {
                                    Id = stickersetDoc["Id"].ToInt64(),
                                    AccessHash = stickersetDoc["AccessHash"].ToInt64()
                                };
                            }
                        }

                        attributes2.Add(stickerAttr);
                    }
                    // Add other attribute types here if needed
                }

                if (attributes2.Count > 0)
                {
                    doc.Attributes = new TVector<IDocumentAttribute>(attributes2);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EnrichMediaDocuments failed for doc {DocumentId}", doc.Id);
        }

        return media;
    }
}
