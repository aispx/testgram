using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text.Json;

namespace MyTelegram.Services.Services;

public class BotCodeQueueOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Durable exchange the delivery bot binds its queue to.</summary>
    public string ExchangeName { get; set; } = "telegram-bot-codes";

    /// <summary>
    /// Durable queue the codes are parked in. Declared by the publisher as well as by the bot, so a code
    /// published while the bot is down is still waiting when it comes back.
    /// </summary>
    public string QueueName { get; set; } = "telegram-bot-codes";

    public string RoutingKey { get; set; } = "code";

    /// <summary>
    /// How long a queued code may wait before the broker drops it. A login code is short lived, so one
    /// that outlived its own expiry is noise rather than something worth delivering.
    /// </summary>
    public int MessageTtlSeconds { get; set; } = 600;
}

/// <summary>
/// Hands a verification code to the Telegram delivery bot.
/// </summary>
/// <remarks>
/// Both callers used to POST to an HTTP endpoint the bot opened on port 5005, which failed in every way
/// it could: the URL was a docker bridge address that does not exist on this host, so each code sat in
/// <c>HttpClient</c> for its full 100 second timeout; the endpoint listened on <c>0.0.0.0</c> with no
/// authentication, so anyone who found the port could make the bot message the owner of any linked
/// number; the two payload shapes disagreed (<c>code</c> against <c>message</c>), so phone verification
/// was silently rejected; and the port was shared with a second copy of the same service, which kept the
/// bot in a systemd restart loop.
///
/// <para>The queue is already part of the stack, needs no open port, and holds a code while the bot is
/// restarting instead of losing it.</para>
/// </remarks>
public interface IBotCodeQueue
{
    bool Enabled { get; }

    /// <summary>
    /// Queues one code for delivery. Never throws: a code that cannot be queued is logged, because
    /// signing in must not fail on the delivery of its code.
    /// </summary>
    Task PublishAsync(string phoneNumber, string code, long? expire = null);
}

public sealed class BotCodeQueue(
    IOptions<BotCodeQueueOptions> options,
    IOptionsMonitor<RabbitMqOptions> rabbitMqOptions,
    ILogger<BotCodeQueue> logger) : IBotCodeQueue, ISingletonDependency, IAsyncDisposable
{
    private const int PublishAttempts = 3;

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public bool Enabled => options.Value.Enabled;

    public async Task PublishAsync(string phoneNumber, string code, long? expire = null)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var body = BuildPayload(phoneNumber, code, expire);

        for (var attempt = 1; attempt <= PublishAttempts; attempt++)
        {
            try
            {
                var channel = await GetChannelAsync();
                await channel.BasicPublishAsync(
                    exchange: options.Value.ExchangeName,
                    routingKey: options.Value.RoutingKey,
                    mandatory: true,
                    basicProperties: new BasicProperties
                    {
                        ContentType = "application/json",
                        DeliveryMode = DeliveryModes.Persistent,
                        Expiration = (options.Value.MessageTtlSeconds * 1000).ToString()
                    },
                    body: body);

                return;
            }
            catch (Exception ex)
            {
                // Rebuild the connection before retrying: a broker restart invalidates both the
                // connection and the channel, and nothing else here fails twice in a row.
                await CloseAsync();

                if (attempt == PublishAttempts)
                {
                    logger.LogError(ex, "Could not queue the code for {Phone} after {Attempts} attempts",
                        phoneNumber, PublishAttempts);
                    return;
                }

                logger.LogWarning(ex, "Queueing the code for {Phone} failed, attempt {Attempt} of {Attempts}",
                    phoneNumber, attempt, PublishAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }

    private static byte[] BuildPayload(string phoneNumber, string code, long? expire)
    {
        // Written by hand rather than serialized from an anonymous type: the bot reads these three names
        // and nothing else, and there is no reflection to trip over in a trimmed build.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("phone", phoneNumber);
            writer.WriteString("code", code);
            if (expire.HasValue)
            {
                writer.WriteNumber("expire", expire.Value);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private async Task<IChannel> GetChannelAsync()
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _connectionGate.WaitAsync();
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            var rabbitMq = rabbitMqOptions.CurrentValue;
            var factory = new ConnectionFactory
            {
                HostName = rabbitMq.HostName,
                Port = rabbitMq.Port,
                UserName = rabbitMq.UserName,
                Password = rabbitMq.Password,
                ClientProvidedName = "MyTelegram.BotCodes",
                AutomaticRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            // Both halves declare the same topology, so whichever starts first creates it.
            await _channel.ExchangeDeclareAsync(options.Value.ExchangeName, ExchangeType.Direct, durable: true);
            await _channel.QueueDeclareAsync(options.Value.QueueName, durable: true, exclusive: false,
                autoDelete: false);
            await _channel.QueueBindAsync(options.Value.QueueName, options.Value.ExchangeName,
                options.Value.RoutingKey);

            logger.LogInformation("Queueing codes to {Exchange}/{RoutingKey} on {Host}:{Port}",
                options.Value.ExchangeName, options.Value.RoutingKey, rabbitMq.HostName, rabbitMq.Port);

            return _channel;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task CloseAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_channel != null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Closing the RabbitMQ connection failed");
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _connectionGate.Dispose();
    }
}
