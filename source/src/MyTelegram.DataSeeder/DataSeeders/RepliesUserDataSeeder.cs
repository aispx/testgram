namespace MyTelegram.DataSeeder.DataSeeders;

/// <summary>
/// Creates the <c>@replies</c> peer. A user who commented on a channel post without joining the
/// discussion group receives replies to their comments as messages from this peer, so it must exist
/// before any comment thread can notify anyone.
/// See https://corefork.telegram.org/api/discussion#replies
/// </summary>
public class RepliesUserDataSeeder(
    ICommandBus commandBus,
    IEventStore eventStore,
    ISnapshotStore snapshotStore,
    ILogger<RepliesUserDataSeeder> logger,
    IDataSeederHelper dataSeederHelper) : IDataSeeder, ITransientDependency
{
    public async Task SeedAsync()
    {
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();
        if (config.IsRepliesUserCreated)
        {
            return;
        }

        var userId = MyTelegramConsts.RepliesServiceUserId;
        var aggregateId = UserId.Create(userId);
        var user = new UserAggregate(aggregateId);
        await user.LoadAsync(eventStore, snapshotStore, CancellationToken.None);

        if (user.IsNew)
        {
            // The request id seeds the deterministic source id of every command the create-user saga
            // chain publishes; leaving it at Guid.Empty makes the follow-up UpdateUserNameCommand2
            // collide with an operation the aggregate has already performed.
            await commandBus.PublishAsync(new CreateUserCommand(aggregateId,
                RequestInfo.Empty with
                {
                    RequestId = Guid.NewGuid(),
                    Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                userId,
                Random.Shared.NextInt64(),
                string.Empty,
                "Replies",
                null,
                "replies",
                false));

            logger.LogInformation("Replies user created successfully");
        }

        await commandBus.PublishAsync(new SetVerifiedCommand(aggregateId, true));

        config.IsRepliesUserCreated = true;
        await dataSeederHelper.SaveDataSeederConfigAsync();
    }
}
