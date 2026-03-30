namespace MyTelegram.SmsSender;

public class TelegramBotSmsOptions
{
    public string SenderApiUrl { get; set; } = "http://127.0.0.1:5005/send";
    public bool Enabled { get; set; } = true;
}

public class TelegramBotSmsSender(
    IOptions<TelegramBotSmsOptions> options,
    ILogger<TelegramBotSmsSender> logger) : ISmsSender
{
    private static readonly HttpClient _http = new();

    public bool Enabled => options.Value.Enabled;

    public async Task SendAsync(SmsMessage smsMessage)
    {
        var text = smsMessage.Text;
        var code = System.Text.RegularExpressions.Regex.Match(text, @"\d{4,8}$").Value;
        if (string.IsNullOrEmpty(code)) code = text;

        try
        {
            var response = await _http.PostAsJsonAsync(options.Value.SenderApiUrl, new
            {
                phone = smsMessage.PhoneNumber,
                code
            });
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("TelegramBotSmsSender: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramBotSmsSender failed for {Phone}", smsMessage.PhoneNumber);
        }
    }
}
