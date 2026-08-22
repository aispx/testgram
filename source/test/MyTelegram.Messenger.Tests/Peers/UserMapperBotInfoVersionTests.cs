using Moq;
using MyTelegram.Converters.Mappers.LatestLayer;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: user serialization.
///
/// <para>
/// <c>user.bot</c> makes the constructor set the <c>bot_info_version</c> flag, and
/// <c>TUser.Serialize</c> then writes <c>BotInfoVersion.Value</c>. A bot whose read model has no
/// version therefore takes down every response it appears in with "Nullable object must have a
/// value" — which is what happened once a system bot that was not created through BotFather started
/// authoring messages.
/// </para>
/// </summary>
public class UserMapperBotInfoVersionTests
{
    [Fact]
    public void A_bot_without_a_recorded_version_still_serializes()
    {
        var user = Map(bot: true, botInfoVersion: null);

        user.BotInfoVersion.ShouldBe(1);
        Should.NotThrow(() => user.ToBytes());
    }

    [Fact]
    public void The_recorded_version_of_a_bot_is_kept()
    {
        Map(bot: true, botInfoVersion: 3).BotInfoVersion.ShouldBe(3);
    }

    [Fact]
    public void A_regular_user_does_not_get_a_bot_version()
    {
        Map(bot: false, botInfoVersion: null).BotInfoVersion.ShouldBeNull();
    }

    private static TUser Map(bool bot, int? botInfoVersion)
    {
        var readModel = new Mock<IUserReadModel>(MockBehavior.Loose);
        readModel.SetupGet(p => p.UserId).Returns(1474613229);
        readModel.SetupGet(p => p.AccessHash).Returns(123);
        readModel.SetupGet(p => p.FirstName).Returns("Imported Message");
        readModel.SetupGet(p => p.PhoneNumber).Returns(string.Empty);
        readModel.SetupGet(p => p.Bot).Returns(bot);
        readModel.SetupGet(p => p.BotInfoVersion).Returns(botInfoVersion);

        return new UserMapper().Map(readModel.Object, new TUser());
    }
}
