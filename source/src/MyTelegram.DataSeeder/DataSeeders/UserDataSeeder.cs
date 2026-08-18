using EventFlow.Queries;
using MyTelegram.Queries;

namespace MyTelegram.DataSeeder.DataSeeders;

public class UserDataSeeder(
    ICommandBus commandBus,
    IEventStore eventStore,
    IQueryProcessor queryProcessor,
    ILogger<UserDataSeeder> logger,
    IOptionsMonitor<MyTelegramDataSeederOptions> options,
    IDataSeederHelper dataSeederHelper,
    ISnapshotStore snapshotStore)
    : IDataSeeder, ITransientDependency
{
    public async Task SeedAsync()
    {
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();

        if (config.IsUserCreated)
        {
            await UpdateServiceNotificationAccountBioAsync();

            return;
        }

        await CreateOfficialUserAsync();
        await CreateDefaultSupportUserAsync();
        await CreateAnonymousUserAsync();
        await CreateGroupAnonymousBotUserAsync();
        await CreateUserIfNeededAsync(MyTelegramConsts.BotFatherUserId, "0", "botfather", null, "botfather", true);

        if (options.CurrentValue.CreateTestUsers)
        {
            var initUserId = MyTelegramConsts.UserIdInitId;
            var testUserCount = 30;
            for (var i = 1; i < testUserCount; i++)
            {
                await CreateUserIfNeededAsync(initUserId + i,
                    $"1{i}",
                    $"{i}",
                    $"{i}",
                    $"user{i}",
                    false);
            }
        }

        config.IsUserCreated = true;
        await dataSeederHelper.SaveDataSeederConfigAsync();
    }

    public async Task<bool> CreateUserIfNeededAsync(long userId,
        string phoneNumber,
        string firstName,
        string? lastName,
        string? userName,
        bool bot)
    {
        var aggregateId = UserId.Create(userId);
        var u = new UserAggregate(aggregateId);
        await u.LoadAsync(eventStore, snapshotStore, CancellationToken.None);
        if (!u.IsNew)
        {
            return false;
        }

        // Some accounts are inserted straight into the read model by an init container rather than
        // through an aggregate (BotFather is), so their aggregate looks new while the user already
        // exists. Creating it again would leave two read-model documents for one user id, and
        // GetUserByIdQuery would then return either of them at random.
        var existingUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        if (existingUser != null)
        {
            logger.LogInformation("User {UserId} already exists outside the event store — skipping", userId);

            return false;
        }

        var accessHash = Random.Shared.NextInt64();
        var createUserCommand =
            new CreateUserCommand(aggregateId,
                // The request id seeds the deterministic source id of every command the create-user
                // saga chain publishes. Left at Guid.Empty, the follow-up UpdateUserNameCommand2 of
                // the second seeded user collides with an operation already performed, and the whole
                // seeding run dies with a DuplicateOperationException.
                RequestInfo.Empty with
                {
                    RequestId = Guid.NewGuid(),
                    Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                userId,
                accessHash,
                phoneNumber,
                firstName,
                lastName,
                userName,
                bot
            );
        await commandBus.PublishAsync(createUserCommand);

        if (userId != MyTelegramConsts.NotificationServiceUserId)
        {
            var command = new UpdateUserPremiumStatusCommand(u.Id, true);
            await commandBus.PublishAsync(command);
        }

        logger.LogInformation("User {UserName} created successfully", userName ?? firstName);

        return true;
    }

    private async Task CreateDefaultSupportUserAsync()
    {
        var userId = MyTelegramConsts.DefaultSupportUserId;
        var created = await CreateUserIfNeededAsync(userId,
            MyTelegramConsts.DefaultSupportUserId.ToString(),
            "Testgram Support",
            null,
            null,
            false);

        if (created)
        {
            var command = new SetSupportCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(command);

            var setVerifiedCommand = new SetVerifiedCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(setVerifiedCommand);
            logger.LogInformation("Testgram support user created successfully");
        }
    }

    private async Task CreateAnonymousUserAsync()
    {
        var userId = MyTelegramConsts.AnonymousUserId;
        var firstName = "Anonymous User";
        await CreateUserIfNeededAsync(userId, string.Empty, firstName, null, null, false);
    }

    /// <summary>
    /// The peer that authors messages sent anonymously by a group admin. Clients resolve it like any
    /// other sender, so it has to exist as a user.
    /// See https://corefork.telegram.org/api/channel#anonymous-admins
    /// </summary>
    private async Task CreateGroupAnonymousBotUserAsync()
    {
        var created = await CreateUserIfNeededAsync(MyTelegramConsts.GroupAnonymousBotUserId,
            string.Empty,
            "Group",
            null,
            null,
            true);

        if (created)
        {
            await commandBus.PublishAsync(
                new SetVerifiedCommand(UserId.Create(MyTelegramConsts.GroupAnonymousBotUserId), true));
        }
    }

    private async Task CreateOfficialUserAsync()
    {
        var userId = MyTelegramConsts.NotificationServiceUserId;
        var created = await CreateUserIfNeededAsync(userId,
            "42777",
            "Testgram",
            null,
            null,
            false);

        if (created)
        {
            var command = new SetSupportCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(command);

            var setVerifiedCommand = new SetVerifiedCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(setVerifiedCommand);
            logger.LogInformation("Testgram notification user created successfully");
        }

        await UpdateServiceNotificationAccountBioAsync();
    }

    private async Task UpdateServiceNotificationAccountBioAsync()
    {
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();
        if (!config.IsServiceNotificationAccountBioUpdated)
        {
            await UpdateUserBioAsync(MyTelegramConsts.NotificationServiceUserId, "Testgram — fork of MyTelegram open-source project.\nRepository: https://github.com/glebxdlolreal/testgram");
            config.IsServiceNotificationAccountBioUpdated = true;
            await dataSeederHelper.SaveDataSeederConfigAsync();
        }
    }

    private Task UpdateUserBioAsync(long userId, string bio)
    {
        var command = new UpdateAboutCommand(UserId.Create(userId), bio);
        return commandBus.PublishAsync(command);
    }
}
