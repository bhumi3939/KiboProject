namespace CommerceHub.API.Events;

/// <summary>
/// Published to RabbitMQ after a successful checkout.
/// Consumers (e.g. notification, fulfillment) listen on the order.created queue.
/// </summary>
public class OrderCreatedEvent
{
    public string OrderId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public List<OrderCreatedItem> Items { get; set; } = new();
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public class OrderCreatedItem
{
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
