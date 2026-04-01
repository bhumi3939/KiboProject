namespace CommerceHub.API.Services;

/// <summary>
/// Abstraction over RabbitMQ so services remain testable without a broker.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes a message to the specified routing key on the configured exchange.
    /// </summary>
    Task PublishAsync<T>(T message, string routingKey, CancellationToken ct = default);
}
