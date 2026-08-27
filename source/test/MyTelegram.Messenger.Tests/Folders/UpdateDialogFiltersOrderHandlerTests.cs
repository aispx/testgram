using System.Reflection;
using EventFlow;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Queries;
using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Folders;

/// <summary>
/// Feature: <c>messages.updateDialogFiltersOrder</c> stores the order.
///
/// <para>It used to answer <c>boolTrue</c> and throw the vector away, so the tab bar reverted on the next
/// <c>getDialogFilters</c>. Clients send <c>0</c> for <c>dialogFilterDefault</c> in the same vector
/// (<c>FilterTabsView</c>), so that value has to be accepted although no folder carries it.</para>
/// </summary>
public class UpdateDialogFiltersOrderHandlerTests
{
    private const long UserId = 2_000_001;

    [Fact]
    public async Task The_order_including_the_default_folder_is_published()
    {
        var fixture = new Fixture([2, 5]);

        var result = await fixture.InvokeAsync([5, 0, 2]);

        // The RPC is answered by the domain event handler, as the other folder writes are.
        result.ShouldBeNull();
        fixture.Published.ShouldHaveSingleItem().Order.ShouldBe([5, 0, 2]);
    }

    [Fact]
    public async Task A_repeated_id_is_kept_once()
    {
        var fixture = new Fixture([2]);

        await fixture.InvokeAsync([0, 2, 2, 0]);

        fixture.Published.ShouldHaveSingleItem().Order.ShouldBe([0, 2]);
    }

    [Fact]
    public async Task An_unknown_folder_id_is_refused()
    {
        var fixture = new Fixture([2]);

        (await Should.ThrowAsync<RpcException>(() => fixture.InvokeAsync([0, 2, 9])))
            .RpcError.Message.ShouldBe("FILTER_ID_INVALID");

        fixture.Published.ShouldBeEmpty();
    }

    private sealed class Fixture
    {
        public List<UpdateDialogFiltersOrderCommand> Published { get; } = [];

        private readonly UpdateDialogFiltersOrderHandler _handler;

        public Fixture(IReadOnlyCollection<int> existingFilterIds)
        {
            var filters = existingFilterIds.Select(id =>
            {
                var filter = new DialogFilter(id, false, false, true, false, false, false, false, false, false,
                    new TTextWithEntities { Text = $"Folder {id}", Entities = [] }, null, null, [], [], [], false);
                var readModel = new Mock<IDialogFilterReadModel>(MockBehavior.Loose);
                readModel.SetupGet(p => p.Filter).Returns(filter);

                return readModel.Object;
            }).ToList();

            var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
            queryProcessor
                .Setup(p => p.ProcessAsync(It.IsAny<GetDialogFiltersQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(filters);

            var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
            commandBus
                .Setup(p => p.PublishAsync(
                    It.IsAny<ICommand<DialogFilterSettingsAggregate, DialogFilterSettingsId, IExecutionResult>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((ICommand<DialogFilterSettingsAggregate, DialogFilterSettingsId, IExecutionResult> command,
                    CancellationToken _) => Published.Add((UpdateDialogFiltersOrderCommand)command))
                .ReturnsAsync(ExecutionResult.Success());

            _handler = new UpdateDialogFiltersOrderHandler(commandBus.Object, queryProcessor.Object);
        }

        public async Task<IBool> InvokeAsync(List<int> order)
        {
            var input = new Mock<IRequestInput>(MockBehavior.Loose);
            input.SetupGet(p => p.UserId).Returns(UserId);

            var request = new RequestUpdateDialogFiltersOrder { Order = new TVector<int>(order) };
            var method = typeof(UpdateDialogFiltersOrderHandler)
                .GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                return await (Task<IBool>)method.Invoke(_handler, [input.Object, request])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
