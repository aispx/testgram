namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Signs in a user with a validated phone number.
/// Possible errors
/// Code Type Description
/// 500 AUTH_RESTART Restart the authorization process.
/// 400 PHONE_CODE_EMPTY phone_code is missing.
/// 400 PHONE_CODE_EXPIRED The phone code you provided has expired.
/// 400 PHONE_CODE_INVALID The provided phone code is invalid.
/// 406 PHONE_NUMBER_INVALID The phone number is invalid.
/// 400 PHONE_NUMBER_UNOCCUPIED The phone number is not yet being used.
/// 500 SIGN_IN_FAILED Failure while signing in.
/// 406 UPDATE_APP_TO_LOGIN Please update your client to login.
/// 420 FLOOD_WAIT_X A wait of X seconds is required.
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.signIn"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class SignInHandler(ICommandBus commandBus, ILogger<SignInHandler> logger, IQueryProcessor queryProcessor, StackExchange.Redis.IConnectionMultiplexer redis) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestSignIn, MyTelegram.Schema.Auth.IAuthorization>
{
    private const int MaxAttemptsPerMinute = 5;
    private const int MaxAttemptsPerHour = 20;

    protected override async Task<MyTelegram.Schema.Auth.IAuthorization> HandleCoreAsync(IRequestInput input, RequestSignIn obj)
    {
        // Rate limiting by phone number
        var phoneNumber = obj.PhoneNumber.ToPhoneNumber();
        await CheckRateLimitAsync(phoneNumber);

        var userId = 0L;
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByPhoneNumberQuery(phoneNumber), default);
        if (userReadModel == null)
        {
            logger.LogInformation("Phone number: {PhoneNumber} does not exists, sign up required", phoneNumber);
        }
        else
        {
            userId = userReadModel.UserId;
        }

        var command = new CheckSignInCodeCommand(AppCodeId.Create(phoneNumber, obj.PhoneCodeHash), input.ToRequestInfo()with { UserId = userId }, obj.PhoneCode ?? string.Empty, userId);
        await commandBus.PublishAsync(command);
        return null !;
    }

    private async Task CheckRateLimitAsync(string phoneNumber)
    {
        var db = redis.GetDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Check per-minute limit
        var keyMinute = $"ratelimit:signin:minute:{phoneNumber}";
        var countMinute = await db.StringIncrementAsync(keyMinute);
        if (countMinute == 1)
        {
            await db.KeyExpireAsync(keyMinute, TimeSpan.FromMinutes(1));
        }

        if (countMinute > MaxAttemptsPerMinute)
        {
            var ttl = await db.KeyTimeToLiveAsync(keyMinute);
            var waitSeconds = (int)(ttl?.TotalSeconds ?? 60);
            logger.LogWarning("Rate limit exceeded for phone {Phone}: {Count} attempts in 1 minute", phoneNumber, countMinute);
            RpcErrors.RpcErrors420.FloodWaitX.ThrowRpcError(waitSeconds);
        }

        // Check per-hour limit
        var keyHour = $"ratelimit:signin:hour:{phoneNumber}";
        var countHour = await db.StringIncrementAsync(keyHour);
        if (countHour == 1)
        {
            await db.KeyExpireAsync(keyHour, TimeSpan.FromHours(1));
        }

        if (countHour > MaxAttemptsPerHour)
        {
            var ttl = await db.KeyTimeToLiveAsync(keyHour);
            var waitSeconds = (int)(ttl?.TotalSeconds ?? 3600);
            logger.LogWarning("Rate limit exceeded for phone {Phone}: {Count} attempts in 1 hour", phoneNumber, countHour);
            RpcErrors.RpcErrors420.FloodWaitX.ThrowRpcError(waitSeconds);
        }
    }
}