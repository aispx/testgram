using MyTelegram.Services.Services;

namespace MyTelegram.SmsSender;

/// <summary>
/// Delivers a login code through the Telegram bot by queueing it (see <see cref="IBotCodeQueue"/>),
/// instead of the HTTP POST to the bot's own port this used to do.
/// </summary>
public class TelegramBotSmsSender(IBotCodeQueue botCodeQueue) : ISmsSender
{
    public bool Enabled => botCodeQueue.Enabled;

    public Task SendAsync(SmsMessage smsMessage)
    {
        // The code and its expiry come as properties: an SMS needs the whole sentence, the bot needs the
        // bare code, and digging it back out of the text with a regular expression was one formatting
        // change away from sending the wrong thing.
        var code = GetProperty(smsMessage, "code") ?? smsMessage.Text;
        var expire = long.TryParse(GetProperty(smsMessage, "expire"), out var parsed) ? parsed : (long?)null;

        return botCodeQueue.PublishAsync(smsMessage.PhoneNumber, code, expire);
    }

    private static string? GetProperty(SmsMessage smsMessage, string name)
    {
        return smsMessage.Properties.TryGetValue(name, out var value) ? value?.ToString() : null;
    }
}
