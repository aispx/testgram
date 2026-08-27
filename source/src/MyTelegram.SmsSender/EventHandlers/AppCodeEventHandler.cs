namespace MyTelegram.SmsSender.EventHandlers;

public class AppCodeEventHandler(ISmsSenderFactory smsSenderFactory, ILogger<AppCodeEventHandler> logger)
    : IEventHandler<AppCodeCreatedIntegrationEvent>, ITransientDependency
{
    public async Task HandleEventAsync(AppCodeCreatedIntegrationEvent eventData)
    {
        var phoneNumber = eventData.PhoneNumber;
        if (!phoneNumber.StartsWith("+"))
        {
            phoneNumber = $"+{phoneNumber}";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (eventData.Expire < now)
        {
            logger.LogWarning("App code expired, data={@Data}", eventData);
            return;
        }

        try
        {
            var smsSender = smsSenderFactory.Create(eventData.PhoneNumber);

            // The code and its expiry travel as properties as well as inside the text: an SMS sender
            // needs the sentence, the Telegram bot needs the bare code, and digging it back out of the
            // text with a regular expression is one formatting change away from sending the wrong thing.
            var smsMessage = new SmsMessage(phoneNumber, $"MyTelegram code: {eventData.Code}");
            smsMessage.Properties["code"] = eventData.Code;
            smsMessage.Properties["expire"] = eventData.Expire;

            await smsSender.SendAsync(smsMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Send sms failed, data={@Data}", eventData);
        }
    }
}
