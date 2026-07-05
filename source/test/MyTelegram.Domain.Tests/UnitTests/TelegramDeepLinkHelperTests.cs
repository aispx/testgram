namespace MyTelegram.Domain.Tests.UnitTests;

public class TelegramDeepLinkHelperTests
{
    [Theory]
    [InlineData("https://t.me/+abc123", "abc123")]
    [InlineData("https://t.me/joinchat/abc123", "abc123")]
    [InlineData("t.me/+abc123", "abc123")]
    [InlineData("abc123", "abc123")]
    [InlineData("tg://join?invite=abc123", "abc123")]
    [InlineData("https://t.me/+abc123#ignored", "abc123")]
    [InlineData("https://t.me/joinchat/abc123?ignored=1#fragment", "abc123")]
    public void GetHashFromLink_ShouldParseSupportedInviteDeepLinks(string link, string expected)
    {
        var helper = new ChatInviteLinkHelper();

        helper.GetHashFromLink(link).ShouldBe(expected);
    }

    [Fact]
    public void GetBusinessChatLink_ShouldUseOfficialMessageSlugPath()
    {
        TelegramDeepLinkHelper.GetBusinessChatLink("slug123").ShouldBe("https://t.me/m/slug123");
    }

    [Theory]
    [InlineData("public_channel", 12345, "https://t.me/boost/public_channel")]
    [InlineData(null, 12345, "https://t.me/boost?c=12345")]
    [InlineData("", 12345, "https://t.me/boost?c=12345")]
    public void GetBoostLink_ShouldUseOfficialPublicAndPrivateFormats(string? username, long channelId, string expected)
    {
        TelegramDeepLinkHelper.GetBoostLink(username, channelId).ShouldBe(expected);
    }

    [Fact]
    public void GetConferenceCallLink_ShouldUseOfficialCallSlugPath()
    {
        TelegramDeepLinkHelper.GetConferenceCallLink("callslug").ShouldBe("https://t.me/call/callslug");
    }

    [Theory]
    [InlineData(false, "invitehash", "https://t.me/public_chat?videochat=invitehash")]
    [InlineData(true, "invitehash", "https://t.me/public_chat?livestream=invitehash")]
    [InlineData(false, null, "https://t.me/public_chat?videochat")]
    [InlineData(true, null, "https://t.me/public_chat?livestream")]
    public void GetGroupCallInviteLink_ShouldUseOfficialVideoChatAndLivestreamFormats(bool livestream, string? inviteHash, string expected)
    {
        TelegramDeepLinkHelper.GetGroupCallInviteLink("public_chat", inviteHash, livestream).ShouldBe(expected);
    }
}
