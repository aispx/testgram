namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Update <a href="https://corefork.telegram.org/api/folders">folder</a>
/// Possible errors
/// Code Type Description
/// 400 CHATLIST_EXCLUDE_INVALID The specified <code>exclude_peers</code> are invalid.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 DIALOG_FILTERS_TOO_MUCH Too many folders.
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_INCLUDE_EMPTY The include_peers vector of the filter is empty.
/// 400 FILTER_INCLUDE_TOO_MUCH Too many chats in the folder.
/// 400 FILTER_TITLE_EMPTY The title field of the filter is empty.
/// 400 MESSAGE_TOO_LONG The provided message is too long.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.updateDialogFilter"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateDialogFilterHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IDialogFilterValidator validator,
    IDialogFilterLimitResolver limitResolver)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUpdateDialogFilter, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateDialogFilter obj)
    {
        var existing = await queryProcessor.ProcessAsync(new GetDialogFilterByIdQuery(input.UserId, obj.Id));

        if (obj.Filter == null)
        {
            // Deleting a folder that is not there is not an error for any client, but the aggregate refuses
            // to emit for an id it never saw, so the command is only worth publishing when there is a row.
            if (existing != null)
            {
                var deleteCommand = new DeleteDialogFilterCommand(DialogFilterId.Create(input.UserId, obj.Id),
                    input.ToRequestInfo());
                await commandBus.PublishAsync(deleteCommand, CancellationToken.None);
            }

            return new TBoolTrue();
        }

        var allFilters = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(input.UserId));

        validator.Validate(new DialogFilterValidationContext(
            obj.Id,
            obj.Filter,
            existing == null,
            existing?.IsShareableFolder == true,
            allFilters.Count,
            await limitResolver.GetFilterLimitAsync(input.UserId),
            await limitResolver.GetChatsPerFilterLimitAsync(input.UserId),
            await limitResolver.GetPinnedPerFilterLimitAsync(input.UserId)));

        var filter = obj.Filter switch
        {
            TDialogFilter f => new DialogFilter(obj.Id, f.Contacts, f.NonContacts, f.Groups, f.Broadcasts, f.Bots,
                f.ExcludeMuted, f.ExcludeRead, f.ExcludeArchived, f.TitleNoanimate, f.Title, f.Emoticon, f.Color,
                GetInputPeers(f.PinnedPeers), GetInputPeers(f.IncludePeers), GetInputPeers(f.ExcludePeers), false),

            // An imported folder is edited with the constructor it was served as: renaming or recolouring a
            // shared folder arrives here as dialogFilterChatlist, which used to throw
            // NotImplementedException. Its peer list and the invite it came from are kept.
            TDialogFilterChatlist c => new DialogFilter(obj.Id, false, false, false, false, false, false, false,
                false, c.TitleNoanimate, c.Title, c.Emoticon, c.Color, GetInputPeers(c.PinnedPeers),
                GetInputPeers(c.IncludePeers), [], true, existing?.ImportedFromSlug),

            _ => null
        };

        if (filter == null)
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        var command = new UpdateDialogFilterCommand(DialogFilterId.Create(input.UserId, obj.Id),
            input.ToRequestInfo(), input.UserId, filter!);
        await commandBus.PublishAsync(command, CancellationToken.None);

        return new TBoolTrue();
    }

    private List<InputPeer> GetInputPeers(TVector<IInputPeer> peers)
    {
        return [.. peers.Select(GetInputPeer)];
    }

    private InputPeer GetInputPeer(IInputPeer inputPeer)
    {
        var peer = peerHelper.GetPeer(inputPeer);
        long accessHash = 0;
        switch (inputPeer)
        {
            case TInputPeerChannel inputPeerChannel:
                accessHash = inputPeerChannel.AccessHash;
                break;
            case TInputPeerChat:
                break;
            case TInputPeerEmpty:
                break;
            case TInputPeerSelf:
                break;
            case TInputPeerUser inputPeerUser:
                accessHash = inputPeerUser.AccessHash;
                break;
            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                break;
        }

        return new InputPeer(peer, accessHash);
    }
}
