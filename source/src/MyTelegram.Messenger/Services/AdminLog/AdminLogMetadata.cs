namespace MyTelegram.Messenger.Services.AdminLog;

/// <summary>
/// Derives the query metadata that is stored alongside every admin log entry: the
/// <a href="https://corefork.telegram.org/constructor/channelAdminLogEventsFilter">filter</a> tags the
/// entry belongs to, the text <c>channels.getAdminLog.q</c> searches in, and the peers referenced by the
/// action so the reader can return them in <c>users</c>/<c>chats</c>.
/// <para>Everything is computed once at write time, so reading is a plain indexed lookup and never has to
/// deserialize the embedded TL blob to decide whether an entry matches.</para>
/// See <a href="https://corefork.telegram.org/api/recent-actions"/>.
/// </summary>
public static class AdminLogMetadata
{
    // Filter tags, named after the flags of channelAdminLogEventsFilter.
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Invite = "invite";
    public const string Ban = "ban";
    public const string Unban = "unban";
    public const string Kick = "kick";
    public const string Unkick = "unkick";
    public const string Promote = "promote";
    public const string Demote = "demote";
    public const string Info = "info";
    public const string Settings = "settings";
    public const string Pinned = "pinned";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string GroupCall = "group_call";
    public const string Invites = "invites";
    public const string Send = "send";
    public const string Forums = "forums";
    public const string SubExtend = "sub_extend";
    public const string EditRank = "edit_rank";

    /// <summary>
    /// view_messages is bit 0 of <a href="https://corefork.telegram.org/constructor/chatBannedRights">chatBannedRights</a>;
    /// it is what separates a kick/unkick from an ordinary restriction change.
    /// </summary>
    private const int ViewMessagesBit = 1;

    public static IReadOnlyCollection<string> Filters(IChannelAdminLogEventAction action)
    {
        return action switch
        {
            TChannelAdminLogEventActionParticipantJoin => [Join],
            TChannelAdminLogEventActionParticipantJoinByInvite => [Join, Invites],
            TChannelAdminLogEventActionParticipantJoinByRequest => [Join, Invites],
            TChannelAdminLogEventActionParticipantLeave => [Leave],
            TChannelAdminLogEventActionParticipantInvite => [Invite],

            TChannelAdminLogEventActionParticipantToggleBan ban =>
                BanFilters(ban.PrevParticipant, ban.NewParticipant),
            TChannelAdminLogEventActionParticipantToggleAdmin admin =>
                AdminFilters(admin.PrevParticipant, admin.NewParticipant),
            TChannelAdminLogEventActionParticipantEditRank => [EditRank],
            TChannelAdminLogEventActionParticipantSubExtend => [SubExtend],

            // Channel information, see the info flag of channelAdminLogEventsFilter.
            TChannelAdminLogEventActionChangeTitle => [Info],
            TChannelAdminLogEventActionChangeAbout => [Info],
            TChannelAdminLogEventActionChangeUsername => [Info],
            TChannelAdminLogEventActionChangeUsernames => [Info],
            TChannelAdminLogEventActionChangePhoto => [Info],
            TChannelAdminLogEventActionChangeStickerSet => [Info],
            TChannelAdminLogEventActionChangeEmojiStickerSet => [Info],
            TChannelAdminLogEventActionChangeLocation => [Info],
            TChannelAdminLogEventActionChangeLinkedChat => [Info],
            TChannelAdminLogEventActionChangeHistoryTTL => [Info],
            TChannelAdminLogEventActionToggleSlowMode => [Info],
            TChannelAdminLogEventActionChangePeerColor => [Info],
            TChannelAdminLogEventActionChangeProfilePeerColor => [Info],
            TChannelAdminLogEventActionChangeEmojiStatus => [Info],
            TChannelAdminLogEventActionChangeWallpaper => [Info],
            TChannelAdminLogEventActionChangeAvailableReactions => [Info],

            // Channel settings, see the settings flag.
            TChannelAdminLogEventActionToggleInvites => [Settings],
            TChannelAdminLogEventActionTogglePreHistoryHidden => [Settings],
            TChannelAdminLogEventActionToggleSignatures => [Settings],
            TChannelAdminLogEventActionToggleSignatureProfiles => [Settings],
            TChannelAdminLogEventActionDefaultBannedRights => [Settings],
            TChannelAdminLogEventActionToggleNoForwards => [Settings],
            TChannelAdminLogEventActionToggleAntiSpam => [Settings],
            TChannelAdminLogEventActionToggleAutotranslation => [Settings],
            // Enabling topics is both a settings change and a forum event.
            TChannelAdminLogEventActionToggleForum => [Settings, Forums],

            TChannelAdminLogEventActionUpdatePinned => [Pinned],
            TChannelAdminLogEventActionDeleteMessage => [Delete],
            TChannelAdminLogEventActionSendMessage => [Send],
            // TDLib documents its edit filter as "changes in messages and polls stopped".
            TChannelAdminLogEventActionEditMessage => [Edit],
            TChannelAdminLogEventActionStopPoll => [Edit],

            TChannelAdminLogEventActionStartGroupCall => [GroupCall],
            TChannelAdminLogEventActionDiscardGroupCall => [GroupCall],
            TChannelAdminLogEventActionToggleGroupCallSetting => [GroupCall],
            TChannelAdminLogEventActionParticipantMute => [GroupCall],
            TChannelAdminLogEventActionParticipantUnmute => [GroupCall],
            TChannelAdminLogEventActionParticipantVolume => [GroupCall],

            TChannelAdminLogEventActionExportedInviteEdit => [Invites],
            TChannelAdminLogEventActionExportedInviteRevoke => [Invites],
            TChannelAdminLogEventActionExportedInviteDelete => [Invites],

            TChannelAdminLogEventActionCreateTopic => [Forums],
            TChannelAdminLogEventActionEditTopic => [Forums],
            TChannelAdminLogEventActionDeleteTopic => [Forums],
            TChannelAdminLogEventActionPinTopic => [Forums],

            _ => []
        };
    }

    /// <summary>
    /// A restriction change is reported as kick/unkick when view_messages changed (the user was thrown out
    /// of the group or let back in) and as ban/unban when any other restriction was added or lifted. Both
    /// can happen at once, which is why the result is a set.
    /// </summary>
    private static IReadOnlyCollection<string> BanFilters(
        MyTelegram.Schema.IChannelParticipant? prevParticipant,
        MyTelegram.Schema.IChannelParticipant? newParticipant)
    {
        var prev = BannedFlags(prevParticipant);
        var current = BannedFlags(newParticipant);

        var added = current & ~prev;
        var removed = prev & ~current;

        var tags = new List<string>(2);

        if ((added & ViewMessagesBit) != 0) tags.Add(Kick);
        if ((removed & ViewMessagesBit) != 0) tags.Add(Unkick);
        if ((added & ~ViewMessagesBit) != 0) tags.Add(Ban);
        if ((removed & ~ViewMessagesBit) != 0) tags.Add(Unban);

        // A no-op edit still belongs to the restriction category, otherwise it would be invisible to
        // every filter the client offers.
        if (tags.Count == 0) tags.Add(current == 0 ? Unban : Ban);

        return tags;
    }

    /// <summary>
    /// Rights granted make it a promotion, rights taken away a demotion; a change limited to the custom
    /// title is reported as edit_rank, matching the dedicated flag added in layer 187.
    /// </summary>
    private static IReadOnlyCollection<string> AdminFilters(
        MyTelegram.Schema.IChannelParticipant? prevParticipant,
        MyTelegram.Schema.IChannelParticipant? newParticipant)
    {
        var prev = AdminFlags(prevParticipant);
        var current = AdminFlags(newParticipant);

        var tags = new List<string>(2);

        if ((current & ~prev) != 0) tags.Add(Promote);
        if ((prev & ~current) != 0) tags.Add(Demote);

        if (!string.Equals(Rank(prevParticipant), Rank(newParticipant), StringComparison.Ordinal))
        {
            tags.Add(EditRank);
        }

        if (tags.Count == 0) tags.Add(current == 0 ? Demote : Promote);

        return tags;
    }

    private static int BannedFlags(MyTelegram.Schema.IChannelParticipant? participant)
    {
        return participant switch
        {
            TChannelParticipantBanned { BannedRights: { } rights } => RightsFlags(rights),
            // A user who left after being kicked keeps no rights object; the ban itself is what matters.
            TChannelParticipantLeft => 0,
            _ => 0
        };
    }

    private static int RightsFlags(IChatBannedRights rights)
    {
        if (rights is TChatBannedRights banned)
        {
            // Flags is only populated by ComputeFlag() during serialization, so a freshly built object
            // may still carry a zero mask while the booleans are set.
            banned.ComputeFlag();
            return banned.Flags;
        }

        return 0;
    }

    private static int AdminFlags(MyTelegram.Schema.IChannelParticipant? participant)
    {
        var rights = participant switch
        {
            TChannelParticipantAdmin admin => admin.AdminRights,
            TChannelParticipantCreator creator => creator.AdminRights,
            _ => null
        };

        if (rights is TChatAdminRights adminRights)
        {
            adminRights.ComputeFlag();
            return adminRights.Flags;
        }

        return 0;
    }

    private static string Rank(MyTelegram.Schema.IChannelParticipant? participant)
    {
        return participant switch
        {
            TChannelParticipantAdmin admin => admin.Rank ?? string.Empty,
            TChannelParticipantCreator creator => creator.Rank ?? string.Empty,
            TChannelParticipantBanned banned => banned.Rank ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// The text <c>q</c> is matched against: message text, titles, usernames, topic titles and invite
    /// links carried by the action itself. Participant names are not stored here — the reader resolves
    /// them against the user read model at query time so a rename does not break the search.
    /// </summary>
    public static string SearchText(IChannelAdminLogEventAction action)
    {
        var parts = new List<string?>();

        switch (action)
        {
            case TChannelAdminLogEventActionChangeTitle title:
                parts.Add(title.PrevValue);
                parts.Add(title.NewValue);
                break;
            case TChannelAdminLogEventActionChangeAbout about:
                parts.Add(about.PrevValue);
                parts.Add(about.NewValue);
                break;
            case TChannelAdminLogEventActionChangeUsername username:
                parts.Add(username.PrevValue);
                parts.Add(username.NewValue);
                break;
            case TChannelAdminLogEventActionChangeUsernames usernames:
                parts.AddRange(usernames.PrevValue ?? []);
                parts.AddRange(usernames.NewValue ?? []);
                break;
            case TChannelAdminLogEventActionParticipantEditRank editRank:
                parts.Add(editRank.PrevRank);
                parts.Add(editRank.NewRank);
                break;
            case TChannelAdminLogEventActionDeleteMessage delete:
                parts.Add(MessageText(delete.Message));
                break;
            case TChannelAdminLogEventActionSendMessage send:
                parts.Add(MessageText(send.Message));
                break;
            case TChannelAdminLogEventActionUpdatePinned pinned:
                parts.Add(MessageText(pinned.Message));
                break;
            case TChannelAdminLogEventActionStopPoll poll:
                parts.Add(MessageText(poll.Message));
                break;
            case TChannelAdminLogEventActionEditMessage edit:
                parts.Add(MessageText(edit.PrevMessage));
                parts.Add(MessageText(edit.NewMessage));
                break;
            case TChannelAdminLogEventActionCreateTopic create:
                parts.Add(TopicTitle(create.Topic));
                break;
            case TChannelAdminLogEventActionDeleteTopic deleteTopic:
                parts.Add(TopicTitle(deleteTopic.Topic));
                break;
            case TChannelAdminLogEventActionEditTopic editTopic:
                parts.Add(TopicTitle(editTopic.PrevTopic));
                parts.Add(TopicTitle(editTopic.NewTopic));
                break;
            case TChannelAdminLogEventActionPinTopic pinTopic:
                parts.Add(TopicTitle(pinTopic.PrevTopic));
                parts.Add(TopicTitle(pinTopic.NewTopic));
                break;
            case TChannelAdminLogEventActionParticipantJoinByInvite joinByInvite:
                parts.Add(InviteText(joinByInvite.Invite));
                break;
            case TChannelAdminLogEventActionParticipantJoinByRequest joinByRequest:
                parts.Add(InviteText(joinByRequest.Invite));
                break;
            case TChannelAdminLogEventActionExportedInviteDelete inviteDelete:
                parts.Add(InviteText(inviteDelete.Invite));
                break;
            case TChannelAdminLogEventActionExportedInviteRevoke inviteRevoke:
                parts.Add(InviteText(inviteRevoke.Invite));
                break;
            case TChannelAdminLogEventActionExportedInviteEdit inviteEdit:
                parts.Add(InviteText(inviteEdit.PrevInvite));
                parts.Add(InviteText(inviteEdit.NewInvite));
                break;
        }

        var text = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        return text.ToLowerInvariant();
    }

    /// <summary>
    /// Users referenced by the action besides its author — the restricted or promoted participant, the
    /// admin who let a join request through, the muted call participant. They have to be returned in
    /// <c>users</c>, otherwise the client renders the entry without a name.
    /// </summary>
    public static IReadOnlyCollection<long> RelatedUserIds(IChannelAdminLogEventAction action)
    {
        var ids = new List<long>(2);

        switch (action)
        {
            case TChannelAdminLogEventActionParticipantToggleBan ban:
                AddParticipant(ids, ban.PrevParticipant);
                AddParticipant(ids, ban.NewParticipant);
                break;
            case TChannelAdminLogEventActionParticipantToggleAdmin admin:
                AddParticipant(ids, admin.PrevParticipant);
                AddParticipant(ids, admin.NewParticipant);
                break;
            case TChannelAdminLogEventActionParticipantSubExtend subExtend:
                AddParticipant(ids, subExtend.PrevParticipant);
                AddParticipant(ids, subExtend.NewParticipant);
                break;
            case TChannelAdminLogEventActionParticipantJoinByRequest joinByRequest:
                ids.Add(joinByRequest.ApprovedBy);
                break;
            case TChannelAdminLogEventActionParticipantEditRank editRank:
                ids.Add(editRank.UserId);
                break;
            case TChannelAdminLogEventActionParticipantInvite invite:
                AddParticipant(ids, invite.Participant);
                break;
            case TChannelAdminLogEventActionParticipantMute mute:
                AddPeer(ids, mute.Participant?.Peer);
                break;
            case TChannelAdminLogEventActionParticipantUnmute unmute:
                AddPeer(ids, unmute.Participant?.Peer);
                break;
            case TChannelAdminLogEventActionParticipantVolume volume:
                AddPeer(ids, volume.Participant?.Peer);
                break;
            case TChannelAdminLogEventActionDeleteMessage delete:
                AddPeer(ids, FromId(delete.Message));
                break;
            case TChannelAdminLogEventActionSendMessage send:
                AddPeer(ids, FromId(send.Message));
                break;
            case TChannelAdminLogEventActionUpdatePinned pinned:
                AddPeer(ids, FromId(pinned.Message));
                break;
            case TChannelAdminLogEventActionEditMessage edit:
                AddPeer(ids, FromId(edit.NewMessage));
                break;
            case TChannelAdminLogEventActionStopPoll poll:
                AddPeer(ids, FromId(poll.Message));
                break;
        }

        return ids.Where(id => id > 0).Distinct().ToList();
    }

    /// <summary>
    /// Channels referenced by the action — currently the discussion group linked to or unlinked from a
    /// channel, which the client has to resolve to draw the entry.
    /// </summary>
    public static IReadOnlyCollection<long> RelatedChannelIds(IChannelAdminLogEventAction action)
    {
        if (action is TChannelAdminLogEventActionChangeLinkedChat linkedChat)
        {
            return new[] { linkedChat.PrevValue, linkedChat.NewValue }
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        return [];
    }

    private static void AddParticipant(List<long> ids, MyTelegram.Schema.IChannelParticipant? participant)
    {
        switch (participant)
        {
            case MyTelegram.Schema.TChannelParticipant p: ids.Add(p.UserId); break;
            case TChannelParticipantSelf p: ids.Add(p.UserId); break;
            case TChannelParticipantAdmin p: ids.Add(p.UserId); break;
            case TChannelParticipantCreator p: ids.Add(p.UserId); break;
            case TChannelParticipantBanned p: AddPeer(ids, p.Peer); break;
            case TChannelParticipantLeft p: AddPeer(ids, p.Peer); break;
        }
    }

    private static void AddPeer(List<long> ids, IPeer? peer)
    {
        if (peer is TPeerUser user)
        {
            ids.Add(user.UserId);
        }
    }

    private static IPeer? FromId(IMessage? message)
    {
        return message switch
        {
            TMessage m => m.FromId,
            TMessageService m => m.FromId,
            _ => null
        };
    }

    private static string? MessageText(IMessage? message)
    {
        return message is TMessage m ? m.Message : null;
    }

    private static string? TopicTitle(IForumTopic? topic)
    {
        return topic is TForumTopic t ? t.Title : null;
    }

    private static string? InviteText(MyTelegram.Schema.IExportedChatInvite? invite)
    {
        return invite switch
        {
            TChatInviteExported exported => $"{exported.Link} {exported.Title}",
            _ => null
        };
    }
}
