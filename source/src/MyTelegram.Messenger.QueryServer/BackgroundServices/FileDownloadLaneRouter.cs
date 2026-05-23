using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger.QueryServer.Services;
using RabbitMQ.Client;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

/// <summary>
/// Keeps legacy download-lane RPCs away from the closed-source file-server binary when
/// they actually belong to messenger. The legacy session-server image publishes
/// messages.getCustomEmojiDocuments as DownloadDataReceivedEvent, while file-server
/// still binds that whole event family and crashes when it tries to deserialize it.
/// </summary>
public sealed class FileDownloadLaneRouter(
    ILogger<FileDownloadLaneRouter> logger,
    IOptionsMonitor<RabbitMqOptions> rabbitMqOptions,
    IRabbitMqSerializer rabbitMqSerializer)
    : BackgroundService, IFileDownloadLaneRouter, IDisposable
{
    private const string SourceExchange = "mytelegram_exchange";
    private const string FilteredExchange = "mytelegram_file_download_exchange";
    private const string FileServerQueue = "MyTelegramFileServer";
    private static readonly string[] RoutedKeys =
    [
        nameof(DownloadDataReceivedEvent),
        nameof(UploadDataReceivedEvent)
    ];

    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task ForwardAsync(DownloadDataReceivedEvent eventData)
    {
        await ForwardAsync(eventData, nameof(DownloadDataReceivedEvent));
    }

    public async Task ForwardAsync(UploadDataReceivedEvent eventData)
    {
        await ForwardAsync(eventData, nameof(UploadDataReceivedEvent));
    }

    private async Task ForwardAsync(DataReceivedEvent eventData, string routingKey)
    {
        using var writer = new CommunityToolkit.HighPerformance.Buffers.ArrayPoolBufferWriter<byte>();
        rabbitMqSerializer.Serialize(writer, eventData);

        await _publishLock.WaitAsync();
        try
        {
            await EnsureTopologyCoreAsync(CancellationToken.None);
            await _channel!.BasicPublishAsync(
                exchange: FilteredExchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: new BasicProperties { DeliveryMode = DeliveryModes.Persistent },
                body: writer.WrittenMemory);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _publishLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureTopologyAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to maintain filtered file download lane");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTopologyCoreAsync(cancellationToken);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task EnsureTopologyCoreAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || !_connection.IsOpen)
        {
            var options = rabbitMqOptions.CurrentValue;
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                AutomaticRecoveryEnabled = true
            };

            _connection?.Dispose();
            _connection = await factory.CreateConnectionAsync(cancellationToken);
        }

        if (_channel is null || !_channel.IsOpen)
        {
            _channel?.Dispose();
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        }

        await _channel.ExchangeDeclareAsync(
            exchange: SourceExchange,
            type: "direct",
            cancellationToken: cancellationToken);
        await _channel.ExchangeDeclareAsync(
            exchange: FilteredExchange,
            type: "direct",
            durable: true,
            cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            queue: FileServerQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        foreach (var routingKey in RoutedKeys)
        {
            await _channel.QueueBindAsync(
                queue: FileServerQueue,
                exchange: FilteredExchange,
                routingKey: routingKey,
                cancellationToken: cancellationToken);
            await _channel.QueueUnbindAsync(
                queue: FileServerQueue,
                exchange: SourceExchange,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: cancellationToken);
        }
    }
}
