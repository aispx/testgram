using System.Reflection;
using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Messenger.Services.Folders;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Schema.Messages;
using MyTelegram.Services.TLObjectConverters;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Folders;

/// <summary>
/// Feature: <c>messages.getDialogFilters</c> replays the stored order and the stored folder tags toggle.
///
/// <para>The order of the answer is the contract: Android numbers <c>filter.order</c> by the position a folder
/// had in this vector, so a reorder that the server does not replay is undone on the next start. The toggle used
/// to be hardcoded to <c>true</c>, which turns folder tags on for every account — the live service answers
/// <c>false</c> for an account without the subscription (measured).</para>
/// See https://corefork.telegram.org/api/folders
/// </summary>
public class GetDialogFiltersHandlerTests
{
    private const long UserId = 2_000_001;

    [Fact]
    public async Task The_stored_order_is_replayed_with_the_default_folder_in_its_slot()
    {
        var fixture = new Fixture([Filter(2), Filter(5), Filter(7)], order: [5, 0, 7, 2]);

        var result = await fixture.InvokeAsync();

        Ids(result).ShouldBe([5, 0, 7, 2]);
    }

    [Fact]
    public async Task The_default_folder_leads_when_the_order_does_not_name_it()
    {
        var fixture = new Fixture([Filter(2), Filter(5)], order: [5, 2]);

        var result = await fixture.InvokeAsync();

        Ids(result).ShouldBe([0, 5, 2]);
    }

    [Fact]
    public async Task A_folder_created_after_the_last_reorder_goes_last()
    {
        var fixture = new Fixture([Filter(2), Filter(4), Filter(9)], order: [0, 9]);

        var result = await fixture.InvokeAsync();

        Ids(result).ShouldBe([0, 9, 2, 4]);
    }

    [Fact]
    public async Task An_order_naming_a_folder_that_is_gone_skips_it()
    {
        var fixture = new Fixture([Filter(2)], order: [0, 6, 2]);

        var result = await fixture.InvokeAsync();

        Ids(result).ShouldBe([0, 2]);
    }

    [Fact]
    public async Task Without_stored_settings_the_default_folder_still_leads()
    {
        var fixture = new Fixture([Filter(3), Filter(2)], order: null);

        var result = await fixture.InvokeAsync();

        Ids(result).ShouldBe([0, 2, 3]);
        result.TagsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task The_tags_toggle_comes_from_the_stored_value()
    {
        var fixture = new Fixture([Filter(2)], order: [0, 2], tagsEnabled: true);

        (await fixture.InvokeAsync()).TagsEnabled.ShouldBeTrue();
    }

    private static List<int> Ids(TDialogFilters filters)
    {
        return
        [
            .. filters.Filters.Select(p => p switch
            {
                TDialogFilterDefault => 0,
                TDialogFilter f => f.Id,
                TDialogFilterChatlist c => c.Id,
                _ => -1
            })
        ];
    }

    private static IDialogFilterReadModel Filter(int filterId)
    {
        var filter = new DialogFilter(filterId, false, false, true, false, false, false, false, false, false,
            new TTextWithEntities { Text = $"Folder {filterId}", Entities = [] }, null, null, [], [], [], false);

        var readModel = new Mock<IDialogFilterReadModel>(MockBehavior.Loose);
        readModel.SetupGet(p => p.OwnerUserId).Returns(UserId);
        readModel.SetupGet(p => p.Filter).Returns(filter);

        return readModel.Object;
    }

    private sealed class Fixture
    {
        private readonly GetDialogFiltersHandler _handler;

        public Fixture(IReadOnlyCollection<IDialogFilterReadModel> filters, IReadOnlyList<int>? order,
            bool tagsEnabled = false)
        {
            var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
            queryProcessor
                .Setup(p => p.ProcessAsync(It.IsAny<GetDialogFiltersQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(filters);

            IDialogFilterSettingsReadModel? settings = null;
            if (order != null || tagsEnabled)
            {
                var settingsMock = new Mock<IDialogFilterSettingsReadModel>(MockBehavior.Loose);
                settingsMock.SetupGet(p => p.Order).Returns(order ?? []);
                settingsMock.SetupGet(p => p.TagsEnabled).Returns(tagsEnabled);
                settings = settingsMock.Object;
            }

            queryProcessor
                .Setup(p => p.ProcessAsync(It.IsAny<GetDialogFilterSettingsQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(settings);

            var inviteStore = new Mock<IChatlistInviteStore>(MockBehavior.Loose);
            inviteStore.Setup(p => p.GetFilterIdsWithInvitesAsync(UserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var converter = new Mock<IDialogFilterConverter>(MockBehavior.Loose);
            converter.Setup(p => p.ToDialogFilter(It.IsAny<DialogFilter>(), It.IsAny<bool>()))
                .Returns((DialogFilter filter, bool _) => new TDialogFilter
                {
                    Id = filter.Id,
                    Title = filter.Title,
                    Groups = filter.Groups,
                    PinnedPeers = [],
                    IncludePeers = [],
                    ExcludePeers = []
                });

            var layeredService = new Mock<ILayeredService<IDialogFilterConverter>>(MockBehavior.Loose);
            layeredService.Setup(p => p.GetConverter(It.IsAny<int>())).Returns(converter.Object);

            _handler = new GetDialogFiltersHandler(queryProcessor.Object,
                new Mock<IAccessHashHelper2>(MockBehavior.Loose).Object,
                inviteStore.Object,
                layeredService.Object);
        }

        public async Task<TDialogFilters> InvokeAsync()
        {
            var input = new Mock<IRequestInput>(MockBehavior.Loose);
            input.SetupGet(p => p.UserId).Returns(UserId);
            input.SetupGet(p => p.Layer).Returns(Layers.LayerLatest);

            var method = typeof(GetDialogFiltersHandler)
                .GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                var result = await (Task<IDialogFilters>)method
                    .Invoke(_handler, [input.Object, new RequestGetDialogFilters()])!;

                return (TDialogFilters)result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
