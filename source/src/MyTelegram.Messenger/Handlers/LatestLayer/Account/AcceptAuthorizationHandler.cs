using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Sends a Telegram Passport authorization form, effectively sharing data with the service
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 PUBLIC_KEY_REQUIRED A public key is required.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.acceptAuthorization"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AcceptAuthorizationHandler(
    IPassportBotResolver passportBotResolver,
    IPassportValueStore passportValueStore,
    IPassportErrorStore passportErrorStore,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestAcceptAuthorization, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestAcceptAuthorization obj)
    {
        await passportBotResolver.ResolveAsync(obj.BotId, obj.PublicKey);

        if (obj.Credentials is not TSecureCredentialsEncrypted credentials
            || credentials.Data.Length == 0
            || credentials.Hash.Length == 0
            || credentials.Secret.Length == 0)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            return null!;
        }

        // The scope is echoed back from the authorization URI; a malformed one means the client is not
        // submitting the form it was shown.
        PassportScopeParser.Parse(obj.Scope);

        var documents = await ResolveValuesAsync(input.UserId, obj.ValueHashes);

        // The bot receives the documents as a service message; nothing is fabricated into the reply,
        // the normal message path delivers updateNewMessage to the bot's sessions.
        // https://corefork.telegram.org/api/passport#receiving-information
        var action = new TMessageActionSecureValuesSentMe
        {
            Values = await passportValueStore.ToSecureValuesAsync(input.UserId, documents),
            Credentials = credentials
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.User, obj.BotId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action);

        await messageAppService.SendMessageAsync([sendInput]);

        // "The user will not be able to re-submit their Passport data to you until the errors are fixed":
        // the form has now been resubmitted, so the previous verdict no longer applies.
        await passportErrorStore.ClearAsync(input.UserId, obj.BotId);

        return new TBoolTrue();
    }

    /// <summary>
    /// "value_hashes is used by the server to choose which document of which type to send to the bot."
    /// The hash must match the one the server issued for that type, so a client cannot make the server
    /// hand over a value the user did not pick.
    /// </summary>
    private async Task<List<PassportValueDocument>> ResolveValuesAsync(long userId,
        TVector<ISecureValueHash>? valueHashes)
    {
        if (valueHashes == null || valueHashes.Count == 0)
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
            return [];
        }

        var types = new List<uint>();
        var expected = new Dictionary<uint, byte[]>();

        foreach (var valueHash in valueHashes.OfType<TSecureValueHash>())
        {
            if (valueHash.Hash.Length != PassportRequestHelper.HashLength)
            {
                RpcErrors.RpcErrors400.HashSizeInvalid.ThrowRpcError();
            }

            if (!PassportValueTypes.IsKnown(valueHash.Type.ConstructorId))
            {
                RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            }

            if (expected.TryAdd(valueHash.Type.ConstructorId, valueHash.Hash.ToArray()))
            {
                types.Add(valueHash.Type.ConstructorId);
            }
        }

        if (types.Count == 0)
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
        }

        var documents = await passportValueStore.GetAsync(userId, types);

        if (documents.Count != types.Count)
        {
            RpcErrors.RpcErrors400.HashInvalid.ThrowRpcError();
        }

        foreach (var document in documents)
        {
            if (!expected.TryGetValue((uint)document.Type, out var hash) || !document.Hash.SequenceEqual(hash))
            {
                RpcErrors.RpcErrors400.HashInvalid.ThrowRpcError();
            }
        }

        return documents;
    }
}
