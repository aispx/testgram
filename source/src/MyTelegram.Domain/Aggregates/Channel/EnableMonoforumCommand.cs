namespace MyTelegram.Domain.Aggregates.Channel;

public partial class EnableMonoforumCommand(
    ChannelId aggregateId,
    RequestInfo requestInfo,
    long channelId,
    bool isMonoforum,
    bool broadcastMessagesAllowed,
    long? linkedMonoforumId
) : RequestCommand2<ChannelAggregate, ChannelId, IExecutionResult>(aggregateId, requestInfo)
{
    public long ChannelId { get; } = channelId;
    public bool IsMonoforum { get; } = isMonoforum;
    public bool BroadcastMessagesAllowed { get; } = broadcastMessagesAllowed;
    public long? LinkedMonoforumId { get; } = linkedMonoforumId;
}

public class EnableMonoforumCommandHandler : CommandHandler<ChannelAggregate, ChannelId, EnableMonoforumCommand>
{
    public override Task ExecuteAsync(ChannelAggregate aggregate, EnableMonoforumCommand command, CancellationToken cancellationToken)
    {
        aggregate.EnableMonoforum(command.RequestInfo, command.ChannelId, command.IsMonoforum, command.BroadcastMessagesAllowed, command.LinkedMonoforumId);
        return Task.CompletedTask;
    }
}
