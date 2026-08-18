namespace MyTelegram.ReadModel.MongoDB;

public class MongoDbIndexesCreator(
    IMongoDatabase database,
    IReadModelDescriptionProvider descriptionProvider,
    IMongoDbEventPersistenceInitializer eventPersistenceInitializer)
    : MongoDbIndexesCreatorBase(database,
        descriptionProvider,
        eventPersistenceInitializer), ITransientDependency
{
    protected override async Task CreateAllIndexesCoreAsync()
    {
        await CreateIndexAsync<DialogReadModel>(p => p.OwnerId);
        await CreateIndexAsync<DialogReadModel>(p => p.Pinned);
        // bots.getBotRecommendations resolves a bot's user base by scanning dialogs pointing at it.
        await CreateIndexAsync<DialogReadModel>(p => p.ToPeerId);

        // The same method's two shapes: the bot's audience (ToPeerId + ToPeerType + IsDeleted, sorted by
        // OwnerId) and the overlap aggregation ($in on OwnerId + ToPeerType + IsDeleted, grouped by
        // ToPeerId). Without these, every private dialog of a popular bot is fetched to re-check flags.
        // IsDeleted is matched with $ne (a range, not an equality), so it must come *after* the sort
        // field: measured on this mongod, putting it before OwnerId still forced an in-memory SORT stage,
        // while this order serves the sort from the index.
        await CreateCompoundIndexAsync<DialogReadModel>("idx_dialog_topeer_type_owner_deleted",
            p => p.ToPeerId, p => p.ToPeerType, p => p.OwnerId, p => p.IsDeleted);
        await CreateCompoundIndexAsync<DialogReadModel>("idx_dialog_owner_type_deleted_topeer",
            p => p.OwnerId, p => p.ToPeerType, p => p.IsDeleted, p => p.ToPeerId);

        await CreateIndexAsync<MessageReadModel>(p => p.MessageId);
        await CreateIndexAsync<MessageReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<MessageReadModel>(p => p.MessageType);
        await CreateIndexAsync<MessageReadModel>(p => p.Pinned);
        await CreateIndexAsync<MessageReadModel>(p => p.Pts);
        await CreateIndexAsync<MessageReadModel>(p => p.ToPeerType);
        await CreateIndexAsync<MessageReadModel>(p => p.SendMessageType);
        // Resolves a poll back to the message carrying it.
        await CreateIndexAsync<MessageReadModel>(p => p.PollId);

        // A message thread is read through either leg of "ReplyToMsgId == root || TopMsgId == root"
        // (see GetMessagesQueryHandler), and both legs are scoped to the chat the thread lives in.
        // See https://corefork.telegram.org/api/threads
        await CreateCompoundIndexAsync<MessageReadModel>("idx_message_owner_replyto_msgid",
            p => p.OwnerPeerId, p => p.ReplyToMsgId, p => p.MessageId);
        await CreateCompoundIndexAsync<MessageReadModel>("idx_message_owner_topmsg_msgid",
            p => p.OwnerPeerId, p => p.TopMsgId, p => p.MessageId);

        await CreateIndexAsync<UserReadModel>(p => p.UserId);
        await CreateIndexAsync<UserReadModel>(p => p.PhoneNumber);
        await CreateIndexAsync<UserReadModel>(p => p.FirstName);
        await CreateIndexAsync<ChannelReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ChannelFullReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.UserId);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.Kicked);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.IsBot);

        // channels.getChannelRecommendations runs two shapes over this collection on every call: the
        // audience sample (ChannelId + Left/Kicked/IsBot, sorted by UserId) and the overlap aggregation
        // ($in on UserId + Left/Kicked, grouped by ChannelId). The single-field indexes above make each
        // one fetch every membership row of the channel just to re-check the boolean flags; these two
        // cover the filters outright. The flags are matched with $ne (a range), so the sort/group field
        // has to precede them - with the flags first, mongod still added an in-memory SORT stage.
        await CreateCompoundIndexAsync<ChannelMemberReadModel>("idx_channelmember_channel_user_flags",
            p => p.ChannelId, p => p.UserId, p => p.Left, p => p.Kicked, p => p.IsBot);
        await CreateCompoundIndexAsync<ChannelMemberReadModel>("idx_channelmember_user_flags_channel",
            p => p.UserId, p => p.Left, p => p.Kicked, p => p.ChannelId);
        //await CreateIndexAsync<AuthKeyReadModel>(p => p.TempAuthKeyId);

        await CreateIndexAsync<DeviceReadModel>(p => p.PermAuthKeyId);
        await CreateIndexAsync<DeviceReadModel>(p => p.UserId);
        await CreateIndexAsync<DeviceReadModel>(p => p.IsActive);

        // Stats ingestion counts muted subscribers per channel (notify_on/muted gauges).
        await CreateIndexAsync<PeerNotifySettingsReadModel>(p => p.PeerId);

        await CreateIndexAsync<ContactReadModel>(p => p.SelfUserId);
        await CreateIndexAsync<ContactReadModel>(p => p.TargetUserId);
        //await CreateIndexAsync<FileReadModel>(p => p.UserId);
        //await CreateIndexAsync<FileReadModel>(p => p.FileId);
        //await CreateIndexAsync<FileReadModel>(p => p.ServerFileId);
        //await CreateIndexAsync<FileReadModel>(p => p.FileReference);

        await CreateIndexAsync<UserNameReadModel>(p => p.UserName);
        await CreateIndexAsync<UserNameReadModel>(p => p.PeerId);

        //await CreateIndexAsync<PushUpdatesReadModel>(p => p.PeerId);
        //await CreateIndexAsync<PushUpdatesReadModel>(p => p.Pts);
        //await CreateIndexAsync<PushUpdatesReadModel>(p => p.SeqNo);

        await CreateIndexAsync<ReadingHistoryReadModel>(p => p.MessageId);
        await CreateIndexAsync<ReadingHistoryReadModel>(p => p.TargetPeerId);

        await CreateIndexAsync<PtsReadModel>(p => p.PeerId);
        await CreateIndexAsync<EncryptedChatReadModel>(p => p.ChatId);
        await CreateIndexAsync<EncryptedChatReadModel>(p => p.AdminId);
        await CreateIndexAsync<EncryptedChatReadModel>(p => p.ParticipantId);

        // updates.getDifference reads this collection on every call, three times over (the pts box, the
        // channel stream and the secret-chat handshake replay), and it is append-only and never pruned.
        // These were previously declared only in QueryServerMongoDbIndexesCreator, which nothing ever
        // invokes - CreateAllIndexesAsync is called from the data seeder alone, and the seeder resolves
        // THIS creator - so the collection ran unindexed.
        await CreateIndexAsync<UpdatesReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<UpdatesReadModel>(p => p.ChannelId);
        await CreateIndexAsync<UpdatesReadModel>(p => p.Pts);
        await CreateIndexAsync<UpdatesReadModel>(p => p.GlobalSeqNo);

        // The handshake replay (GetUpdatesByGlobalSeqNoQuery) filters OwnerPeerId + UpdatesType and then
        // ranges and sorts on GlobalSeqNo. The single-field indexes above cannot serve that shape: measured
        // against a 84k-row collection, OwnerPeerId_1 alone still fetched 13k documents and blocked on an
        // in-memory sort (61ms), while this compound index answers it from the index (0 documents, 1ms).
        // Field order matters - the two equality fields must precede the range/sort field.
        await CreateCompoundIndexAsync<UpdatesReadModel>("idx_updates_owner_type_seq",
            p => p.OwnerPeerId, p => p.UpdatesType, p => p.GlobalSeqNo);

        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.PeerId);
        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.PermAuthKeyId);
        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.GlobalSeqNo);
        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.Pts);

        await CreateIndexAsync<RpcResultReadModel>(p => p.ReqMsgId);

        await CreateIndexAsync<ReplyReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ReplyReadModel>(p => p.MessageId);

        await CreateIndexAsync<DialogFilterReadModel>(p => p.OwnerUserId);
        await CreateIndexAsync<PollReadModel>(p => p.ToPeerId);
        await CreateIndexAsync<PollReadModel>(p => p.PollId);
        // Scanned by the poll auto-close background service.
        await CreateIndexAsync<PollReadModel>(p => p.CloseDate);
        await CreateIndexAsync<PollAnswerVoterReadModel>(p => p.PollId);
        await CreateIndexAsync<PollAnswerVoterReadModel>(p => p.Option);
        await CreateIndexAsync<PollAnswerVoterReadModel>(p => p.VoterPeerId);
        // Recent voters are read newest-first.
        await CreateIndexAsync<PollAnswerVoterReadModel>(p => p.Date);

        await CreateIndexAsync<LanguageReadModel>(p => p.LanguageCode);
        await CreateIndexAsync<LanguageTextReadModel>(p => p.LanguageCode);
        await CreateIndexAsync<LanguageTextReadModel>(p => p.Platform);

		await CreateIndexAsync<UserConfigReadModel>(p => p.UserId);
        await CreateIndexAsync<UserConfigReadModel>(p => p.Key);

        await CreateIndexAsync<MessageTokenReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<MessageTokenReadModel>(p => p.ToPeerId);
        await CreateIndexAsync<MessageTokenReadModel>(p => p.MessageId);
        await CreateIndexAsync<MessageTokenReadModel>(p => p.Tokens);

        var snapShotCollectionName = "snapShots";
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateId, snapShotCollectionName);
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateName, snapShotCollectionName);
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateSequenceNumber, snapShotCollectionName);

        // The four messages.getEmoji*Groups methods each filter emoji_groups on For and sort by
        // Order, Title. The collection is seeded outside EventFlow, so it only had the default
        // _id_ index and every category lookup was a collection scan.
        await CreateRawIndexAsync("emoji_groups", "For", "Order", "Title");
    }
}
