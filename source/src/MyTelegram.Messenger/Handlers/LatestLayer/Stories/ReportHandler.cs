using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Report a story.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_INVALID The specified story ID is invalid.
/// 400 OPTION_INVALID Invalid option selected.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.report"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Follows the two-step <a href="https://corefork.telegram.org/api/reports">report flow</a>: an empty
/// <c>option</c> returns the list of reasons to choose from, and a chosen option files the report.
/// </para>
/// </remarks>
internal sealed class ReportHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestReport, IReportResult>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _reportsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reports");

    protected override async Task<IReportResult> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestReport obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var storyIds = obj.Id?.Distinct().ToList() ?? [];
        if (storyIds.Count == 0)
        {
            RpcErrors.RpcErrors400.StoryIdEmpty.ThrowRpcError();
        }

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();
        if (stories.Count == 0)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        // First step: the client has not picked a reason yet.
        if (obj.Option.Length == 0)
        {
            return BuildOptionList();
        }

        var option = ReportOptions.Find(obj.Option);
        if (option == null)
        {
            RpcErrors.RpcErrors400.OptionInvalid.ThrowRpcError();
        }

        var date = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var story in stories)
        {
            await _reportsCollection.InsertOneAsync(new BsonDocument
            {
                { "reporterUserId", input.UserId },
                { "ownerPeerId", peerId },
                { "ownerPeerType", peerType },
                { "storyId", story.StoryId },
                { "reason", option!.Value.Id },
                { "message", obj.Message ?? string.Empty },
                { "date", date }
            });
        }

        await _storyCollection.UpdateManyAsync(
            filter,
            Builders<StoryDocument>.Update.Set(s => s.Reported, true));

        return new TReportResultReported();
    }

    private static IReportResult BuildOptionList()
    {
        var options = new TVector<IMessageReportOption>();

        foreach (var option in ReportOptions.All)
        {
            options.Add(new TMessageReportOption
            {
                Text = option.Text,
                Option = Encoding.UTF8.GetBytes(option.Id)
            });
        }

        return new TReportResultChooseOption
        {
            Title = "Report Story",
            Options = options
        };
    }

    /// <summary>
    /// The report reasons offered for stories. The opaque <c>option</c> bytes the client echoes back are
    /// just the UTF-8 reason id.
    /// </summary>
    private static class ReportOptions
    {
        internal readonly record struct Option(string Id, string Text);

        internal static readonly Option[] All =
        [
            new("spam", "Spam"),
            new("violence", "Violence"),
            new("pornography", "Pornography"),
            new("child_abuse", "Child Abuse"),
            new("copyright", "Copyright"),
            new("illegal_drugs", "Illegal Drugs"),
            new("personal_details", "Personal Details"),
            new("other", "Other")
        ];

        internal static Option? Find(ReadOnlyMemory<byte> rawOption)
        {
            var id = Encoding.UTF8.GetString(rawOption.Span);

            foreach (var option in All)
            {
                if (option.Id == id)
                {
                    return option;
                }
            }

            return null;
        }
    }
}
