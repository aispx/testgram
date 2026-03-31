using MyTelegram.Schema;
using TBusinessWorkHours = MyTelegram.Schema.TBusinessWorkHours;
using TBusinessLocation = MyTelegram.Schema.TBusinessLocation;
using TBusinessGreetingMessage = MyTelegram.Schema.TBusinessGreetingMessage;
using TBusinessAwayMessage = MyTelegram.Schema.TBusinessAwayMessage;
using TBusinessIntro = MyTelegram.Schema.TBusinessIntro;

namespace MyTelegram.ReadModel.Interfaces;

public interface IUserFullReadModel : IReadModel
{
    string Id { get; }
    long UserId { get; }
    TBusinessWorkHours? BusinessWorkHours { get; set; }
    TBusinessLocation? BusinessLocation { get; }
    TBusinessGreetingMessage? BusinessGreetingMessage { get; set; }
    TBusinessAwayMessage? BusinessAwayMessage { get; set; }
    TBusinessIntro? BusinessIntro { get; }
}