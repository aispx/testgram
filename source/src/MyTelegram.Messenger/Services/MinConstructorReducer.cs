namespace MyTelegram.Messenger.Services;

/// <summary>
/// Reduces the <c>user</c> and <c>channel</c> constructors of a message-bearing response to their
/// <a href="https://corefork.telegram.org/api/min">min</a> form.
/// <para>
/// A peer the caller is only seeing because it turned up in somebody else's message is sent with the
/// <c>min</c> flag and without the fields that describe a relationship with it. The client keeps the
/// context it was seen in — the chat plus the message id — and addresses the peer afterwards through
/// an <c>input*FromMessage</c> constructor rather than an access hash.
/// </para>
/// <para>
/// <b>Why this is unconditional.</b> A min constructor cannot lose a client any data, because the
/// merge rules make it additive: the <a href="https://corefork.telegram.org/constructor/user">user</a>
/// and <a href="https://corefork.telegram.org/constructor/channel">channel</a> pages name exactly the
/// fields a min constructor is not allowed to apply over a locally cached one, and those are the only
/// fields cleared here (see <see cref="ReduceUser"/> and <see cref="ReduceChannel"/>). A client that
/// already knows the peer in full keeps everything it had; a client that does not gets the identity
/// fields plus a citable context. So there is no state in which sending min is worse than sending the
/// whole thing, and therefore nothing to switch off.
/// </para>
/// <para>
/// The one hard requirement is that the peer stays reachable: it has to be named by a message in the
/// very same response, and that message has to live in a channel or supergroup, because that is the
/// container an <c>input*FromMessage</c> constructor names. The predicate deciding "is this peer named
/// by this message" is <see cref="MessagePeerReferences"/> — the same one
/// <see cref="IFromMessagePeerResolver"/> validates incoming citations with, so every peer reduced
/// here can be cited back successfully.
/// </para>
/// </summary>
public interface IMinConstructorReducer
{
    /// <summary>
    /// Marks the eligible entries of <paramref name="users"/> and <paramref name="chats"/> as
    /// <c>min</c>, in place.
    /// </summary>
    /// <param name="request">The caller the response is being built for.</param>
    /// <param name="messages">The messages travelling in the same response — the citable contexts.</param>
    void Reduce(
        IRequestWithAccessHashKeyId request,
        IReadOnlyCollection<IMessageReadModel> messages,
        IReadOnlyCollection<ILayeredUser> users,
        IReadOnlyCollection<IChat> chats);
}

public sealed class MinConstructorReducer(IPeerHelper peerHelper)
    : IMinConstructorReducer, ITransientDependency
{
    public void Reduce(
        IRequestWithAccessHashKeyId request,
        IReadOnlyCollection<IMessageReadModel> messages,
        IReadOnlyCollection<ILayeredUser> users,
        IReadOnlyCollection<IChat> chats)
    {
        if (messages.Count == 0 || (users.Count == 0 && chats.Count == 0))
        {
            return;
        }

        // A bot could never act on a min peer: IFromMessagePeerResolver refuses fromMessage citations
        // from bots outright (FROM_MESSAGE_BOT_DISABLED), so reducing here would hand a bot a peer with
        // no way left to address it. Bots get full constructors.
        if (peerHelper.IsBotUser(request.UserId))
        {
            return;
        }

        // Only a message in a channel or supergroup can be cited: inputPeer*FromMessage names the
        // container chat, and Testgram keeps a private message under each participant's own id, so a
        // private conversation has no chat to name.
        var citableMessages = messages
            .Where(p => peerHelper.IsChannelPeer(p.OwnerPeerId))
            .ToList();
        if (citableMessages.Count == 0)
        {
            return;
        }

        // The chats the response is *about* keep their full constructor. They are not peers glimpsed in
        // passing — the caller asked for this history — and withholding the participant count of the
        // chat being read on every page of it would be pointless churn.
        var containerIds = citableMessages.Select(p => p.OwnerPeerId).ToHashSet();

        foreach (var user in users)
        {
            // Self is the one user a citation is never needed for, and never right for.
            if (user is TUser tUser &&
                tUser.Id != request.UserId &&
                !tUser.Self &&
                citableMessages.Any(p => MessagePeerReferences.ReferencesUser(p, tUser.Id)))
            {
                ReduceUser(tUser);
            }
        }

        foreach (var chat in chats)
        {
            if (chat is TChannel channel &&
                !containerIds.Contains(channel.Id) &&
                citableMessages.Any(p => MessagePeerReferences.ReferencesChannel(p, channel.Id)))
            {
                ReduceChannel(channel);
            }
        }
    }

    /// <summary>
    /// Clears the fields the <a href="https://corefork.telegram.org/constructor/user">user</a> page
    /// marks as "do not apply changes to this field if the min flag is set", and nothing else. Each one
    /// describes a relationship between the caller and this user, which is what a min constructor does
    /// not speak for — and because the client is required to keep its cached value for exactly these
    /// fields, clearing them costs it nothing.
    /// <para>
    /// The identity fields are deliberately left alone. For a min user the spec protects a named list
    /// and applies everything else, <i>including by removing fields that aren't set</i>, so dropping
    /// e.g. <c>first_name</c> here would erase it from the client's cache.
    /// </para>
    /// <para>
    /// Withholding the phone number is also what makes the client derive <c>min_access_hash</c> as
    /// true, and therefore prefer an <c>input*FromMessage</c> constructor over the bare access hash.
    /// The hash itself stays: on a min user it remains valid for <c>inputPeerPhotoFileLocation</c>, so
    /// profile pictures keep loading.
    /// </para>
    /// </summary>
    private static void ReduceUser(TUser user)
    {
        user.Phone = null;
        user.Contact = false;
        user.MutualContact = false;
        user.CloseFriend = false;
        user.AttachMenuEnabled = false;
        user.BotCanEdit = false;
        user.StoriesHidden = false;

        // A photo on a min user may only update the local database when apply_min_photo says so, and
        // ours is the real current photo, so it does.
        if (user.Photo is not null and not TUserProfilePhotoEmpty)
        {
            user.ApplyMinPhoto = true;
        }

        user.Min = true;
    }

    /// <summary>
    /// Keeps the fields the <a href="https://corefork.telegram.org/constructor/channel">channel</a>
    /// page lists as the ones a min constructor may apply, and clears the rest: participant counts,
    /// call state and the caller's own membership, restrictions and subscription. As with
    /// <see cref="ReduceUser"/>, every field cleared here is one the client keeps its cached value for.
    /// <para>
    /// <c>creator</c> and <c>admin_rights</c> are equally protected but left as they are, because the
    /// merge ignores them either way and a caller reading their own channel's history should not see
    /// its own rights blink.
    /// </para>
    /// <para>
    /// <c>stories_hidden</c> becomes <c>stories_hidden_min</c>: the flag was not populated, so the
    /// client is told to keep whatever it had rather than read false as "not hidden".
    /// </para>
    /// </summary>
    private static void ReduceChannel(TChannel channel)
    {
        channel.Left = false;
        channel.BannedRights = null;
        channel.ParticipantsCount = null;
        channel.SubscriptionUntilDate = null;
        channel.CallActive = false;
        channel.CallNotEmpty = false;
        channel.StoriesHidden = false;
        channel.StoriesHiddenMin = true;

        channel.Min = true;
    }
}
