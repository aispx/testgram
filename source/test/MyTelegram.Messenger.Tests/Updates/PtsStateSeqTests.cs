using EventFlow.Aggregates;
using EventFlow.ReadStores;
using MyTelegram.Domain.Aggregates.Pts;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.TLObjectConverters.Mappers;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema.Updates;
using PersistedPtsReadModel = MyTelegram.ReadModel.Impl.PtsReadModel;

namespace MyTelegram.Messenger.Tests.Updates;

/// <summary>
/// Regression tests for the <c>seq</c> reported in <c>updates.state</c>.
///
/// <para><see cref="CustomObjectMapper"/> populated Date, Pts, Qts and UnreadCount but left Seq at
/// its default of 0, while every fallback branch in the difference converter and
/// <c>updates.getState</c> reported 1. A client that had already seen seq = 1 treated the state as
/// stale, never advanced its cursor, and re-issued <c>updates.getDifference</c> immediately — an
/// endless sync loop (observed at roughly four calls per second with nothing left to deliver).
/// </para>
/// </summary>
public class PtsStateSeqTests
{
    private sealed class FakePtsReadModel : IPtsReadModel
    {
        public int Date { get; init; }
        public long GlobalSeqNo { get; init; }
        public string Id { get; init; } = "pts-test";
        public long PeerId { get; init; }
        public int Pts { get; init; }
        public int Qts { get; init; }
        public int Seq { get; init; }
        public int UnreadCount { get; init; }
        public int MaxMessageId { get; init; }
    }

    private static TState Map(IPtsReadModel source)
    {
        return new CustomObjectMapper().Map(source, new TState());
    }

    [Fact]
    public void Map_CarriesTheStoredSeq()
    {
        var state = Map(new FakePtsReadModel { PeerId = 2010001, Pts = 363013, Seq = 42 });

        state.Seq.ShouldBe(42);
    }

    [Fact]
    public void Map_NeverReportsSeqZero_ForRowsWrittenBeforeSeqExisted()
    {
        // A document persisted before the field existed deserialises with Seq = 0. Reporting that
        // would push the client's seq backwards and restart the loop this test exists to prevent.
        var state = Map(new FakePtsReadModel { PeerId = 2010001, Pts = 363013, Seq = 0 });

        state.Seq.ShouldBe(1);
    }

    [Fact]
    public void Map_StillCarriesTheOtherStateFields()
    {
        var state = Map(new FakePtsReadModel
        {
            PeerId = 2010001,
            Pts = 363013,
            Qts = 7,
            Date = 1785880332,
            UnreadCount = 3,
            Seq = 5
        });

        state.Pts.ShouldBe(363013);
        state.Qts.ShouldBe(7);
        state.Date.ShouldBe(1785880332);
        state.UnreadCount.ShouldBe(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(99, 99)]
    public void PtsCacheItem_FloorsSeqAtOne(int storedSeq, int expected)
    {
        var item = new PtsCacheItem(2010001, pts: 1, qts: 0, date: 0, seq: storedSeq);

        item.Seq.ShouldBe(expected);
    }

    [Fact]
    public void PtsCacheItem_DefaultsSeqToOne_WhenTheBoxHasNoStoredState()
    {
        var item = new PtsCacheItem(2010001);

        item.Seq.ShouldBe(1);
    }

    [Fact]
    public async Task ReadModel_AdvancesSeqOnEveryAppliedPtsUpdate()
    {
        const long peerId = 2010001;
        var readModel = new PersistedPtsReadModel();

        for (var i = 1; i <= 3; i++)
        {
            await ApplyPtsAsync(readModel, peerId, newPts: i);
        }

        readModel.Seq.ShouldBe(3);
        readModel.Pts.ShouldBe(3);
    }

    [Fact]
    public async Task ReadModel_DoesNotAdvanceSeqForAnOutOfOrderReplay()
    {
        // The read model ignores a pts that does not move the box forward; seq must not drift either,
        // or a redelivered event would inflate it past what the client was actually sent.
        const long peerId = 2010001;
        var readModel = new PersistedPtsReadModel();

        await ApplyPtsAsync(readModel, peerId, newPts: 5);
        await ApplyPtsAsync(readModel, peerId, newPts: 3);

        readModel.Seq.ShouldBe(1);
        readModel.Pts.ShouldBe(5);
    }

    [Fact]
    public async Task ReadModel_ReportsSeqAtLeastOne_AfterItsFirstUpdate()
    {
        var readModel = new PersistedPtsReadModel();

        await ApplyPtsAsync(readModel, peerId: 2010001, newPts: 1);

        Map(readModel).Seq.ShouldBe(1);
    }

    private static Task ApplyPtsAsync(PersistedPtsReadModel readModel, long peerId, int newPts)
    {
        var aggregateEvent = new PtsUpdatedEvent(
            peerId,
            permAuthKeyId: 0,
            newPts,
            date: 1785880332,
            globalSeqNo: newPts,
            changedUnreadCount: 0,
            messageId: null);

        var domainEvent = new DomainEvent<PtsAggregate, PtsId, PtsUpdatedEvent>(
            aggregateEvent,
            Metadata.Empty,
            DateTimeOffset.UtcNow,
            PtsId.Create(peerId),
            newPts);

        return readModel.ApplyAsync(
            new ReadModelContext(serviceProvider: null!, readModel.Id ?? PtsId.Create(peerId).Value, isNew: false),
            domainEvent,
            CancellationToken.None);
    }
}
