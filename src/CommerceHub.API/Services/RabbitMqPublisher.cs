using System.Text;
using System.Text.Json;
using CommerceHub.API.Configuration;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CommerceHub.API.Services;

/// <summary>
/// Persistent RabbitMQ publisher. Channel and connection are reused across calls.
/// Exchange is declared durable on startup so messages survive broker restarts.
/// </summary>
public class RabbitMqPublisher : IMessagePublisher, IDisposable
{
    private readonly RabbitMqSettings _settings;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private bool _disposed;

    public RabbitMqPublisher(IOptions<RabbitMqSettings> settings, ILogger<RabbitMqPublisher> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        InitializeRabbitMq();
    }

    private void InitializeRabbitMq()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _settings.HostName,
                Port = _settings.Port,
                UserName = _settings.UserName,
                Password = _settings.Password,
                // Auto-recovery so transient network blips don't kill the publisher
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Declare a durable topic exchange
            _channel.ExchangeDeclare(
                exchange: _settings.ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false
            );

            // Declare and bind the order.created queue
            _channel.QueueDeclare(
                queue: _settings.OrderCreatedQueue,
                durable: true,
                exclusive: false,
                autoDelete: false
            );

            _channel.QueueBind(
                queue: _settings.OrderCreatedQueue,
                exchange: _settings.ExchangeName,
                routingKey: _settings.OrderCreatedRoutingKey
            );

            _logger.LogInformation("RabbitMQ connected to {Host}", _settings.HostName);
        }
        catch (Exception ex)
        {
            // Non-fatal on startup — log and continue. Health checks will surface this.
            _logger.LogError(ex, "Failed to connect to RabbitMQ on startup");
        }
    }

    public Task PublishAsync<T>(T message, string routingKey, CancellationToken ct = default)
    {
        if (_channel is null || !_channel.IsOpen)
        {
            _logger.LogWarning("RabbitMQ channel not available — message dropped");
            return Task.CompletedTask;
        }

        var json = JsonSerializer.Serialize(message);
        var body = Encoding.UTF8.GetBytes(json);

        var props = _channel.CreateBasicProperties();
        props.Persistent = true;           // messages survive broker restart
        props.ContentType = "application/json";
        props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _channel.BasicPublish(
            exchange: _settings.ExchangeName,
            routingKey: routingKey,
            basicProperties: props,
            body: body
        );

        _logger.LogInformation("Published {RoutingKey} message for routing key {Key}", typeof(T).Name, routingKey);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _channel?.Close();
        _channel?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
        _disposed = true;
    }
}
