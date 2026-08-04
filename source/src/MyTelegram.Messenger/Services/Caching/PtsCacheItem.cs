namespace MyTelegram.Messenger.Services.Caching;

public class PtsCacheItem(long ownerPeerId, int pts = 1, int qts = 0, int date = 0, int seq = 1)
{
    private int _unreadCount;
    private int _pts = pts;
    private int _qts = qts;
    public long OwnerPeerId { get; } = ownerPeerId;
    public int Date { get; } = date;
    public int Pts => _pts;

    public int Qts => _qts;

    /// <summary>
    /// The <c>seq</c> last written to the read model. Clients refuse to apply a state whose seq
    /// went backwards, so this must stay at 1 or above even for a box that has no stored value yet.
    /// </summary>
    public int Seq { get; } = seq < 1 ? 1 : seq;
    public int UnreadCount => _unreadCount;

    public void IncrementPts()
    {
        Interlocked.Increment(ref _pts);
    }

    public void AddUnreadCount(int unreadCount)
    {
        Interlocked.Add(ref _unreadCount, unreadCount);
    }

    public void AddPts(int ptsCount)
    {
        Interlocked.Add(ref _pts, ptsCount);
    }

    public void AddQts(int qtsCount)
    {
        Interlocked.Add(ref _qts, qtsCount);
    }

    public void IncrementQts()
    {
        Interlocked.Increment(ref _qts);
    }
}