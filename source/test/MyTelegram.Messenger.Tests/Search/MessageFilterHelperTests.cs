using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Search;

/// <summary>
/// Covers the mapping from a TL message filter to the internal message types used to query the
/// read model. See https://corefork.telegram.org/api/search#filtering-by-message-type
/// </summary>
public class MessageFilterHelperTests
{
    [Fact]
    public void ShouldNotFilterByTypeWithoutAFilter()
    {
        MessageFilterHelper.GetMessageTypes(null).ShouldBeEmpty();
        MessageFilterHelper.GetMessageTypes(new TInputMessagesFilterEmpty()).ShouldBeEmpty();
    }

    [Fact]
    public void ShouldMatchPhotosAndVideosForTheCombinedFilter()
    {
        // The media tab uses photoVideo; returning only videos would silently hide every photo.
        var types = MessageFilterHelper.GetMessageTypes(new TInputMessagesFilterPhotoVideo());

        types.ShouldContain(MessageType.Photo);
        types.ShouldContain(MessageType.Video);
    }

    [Fact]
    public void ShouldMatchDocumentForMediaFiltersBecauseMediaIsStoredAsDocument()
    {
        // MediaHelper classifies every TMessageMediaDocument as Document, so a filter that only
        // looked for its "natural" type would come back empty.
        foreach (var filter in new IMessagesFilter[]
                 {
                     new TInputMessagesFilterVideo(),
                     new TInputMessagesFilterRoundVideo(),
                     new TInputMessagesFilterVoice(),
                     new TInputMessagesFilterRoundVoice(),
                     new TInputMessagesFilterMusic(),
                     new TInputMessagesFilterGif()
                 })
        {
            MessageFilterHelper.GetMessageTypes(filter).ShouldContain(MessageType.Document,
                $"{filter.GetType().Name} must still match documents");
        }
    }

    [Theory]
    [InlineData(typeof(TInputMessagesFilterPhotos), MessageType.Photo)]
    [InlineData(typeof(TInputMessagesFilterChatPhotos), MessageType.Photo)]
    [InlineData(typeof(TInputMessagesFilterDocument), MessageType.Document)]
    [InlineData(typeof(TInputMessagesFilterUrl), MessageType.Url)]
    [InlineData(typeof(TInputMessagesFilterGeo), MessageType.Geo)]
    [InlineData(typeof(TInputMessagesFilterContacts), MessageType.Contacts)]
    [InlineData(typeof(TInputMessagesFilterPoll), MessageType.Poll)]
    [InlineData(typeof(TInputMessagesFilterPhoneCalls), MessageType.PhoneCall)]
    public void ShouldMapSingleTypeFilters(Type filterType, MessageType expected)
    {
        var filter = (IMessagesFilter)Activator.CreateInstance(filterType)!;

        MessageFilterHelper.GetMessageTypes(filter).ShouldContain(expected);
    }

    [Fact]
    public void ShouldExpressPinnedAsAFlagRatherThanAType()
    {
        var filter = new TInputMessagesFilterPinned();

        MessageFilterHelper.IsPinnedFilter(filter).ShouldBeTrue();
        // Pinned messages keep their own type, so filtering by type as well would return nothing.
        MessageFilterHelper.GetMessageTypes(filter).ShouldBeEmpty();
    }

    [Fact]
    public void ShouldExpressMyMentionsAsAFlagRatherThanAType()
    {
        var filter = new TInputMessagesFilterMyMentions();

        MessageFilterHelper.IsMyMentionsFilter(filter).ShouldBeTrue();
        MessageFilterHelper.GetMessageTypes(filter).ShouldBeEmpty();
    }

    [Fact]
    public void ShouldRejectEmptyAndMyMentionsForPositionsAndCalendar()
    {
        MessageFilterHelper.IsSupportedByPositionsAndCalendar(null).ShouldBeFalse();
        MessageFilterHelper.IsSupportedByPositionsAndCalendar(new TInputMessagesFilterEmpty()).ShouldBeFalse();
        MessageFilterHelper.IsSupportedByPositionsAndCalendar(new TInputMessagesFilterMyMentions()).ShouldBeFalse();
        MessageFilterHelper.IsSupportedByPositionsAndCalendar(new TInputMessagesFilterPhotos()).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRejectAnUnknownFilterWithAnRpcError()
    {
        // An unknown constructor must surface as INPUT_FILTER_INVALID (400), not as a 500.
        Should.Throw<RpcException>(() => MessageFilterHelper.GetMessageTypes(new UnknownFilter()))
            .RpcError.Message.ShouldBe("INPUT_FILTER_INVALID");
    }

    private sealed class UnknownFilter : IMessagesFilter
    {
        public uint ConstructorId => 0xdeadbeef;

        public void ComputeFlag()
        {
        }

        public void Serialize(System.Buffers.IBufferWriter<byte> writer)
        {
        }

        public void Deserialize(ref ReadOnlyMemory<byte> buffer)
        {
        }
    }
}
