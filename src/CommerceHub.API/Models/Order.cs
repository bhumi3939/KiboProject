using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CommerceHub.API.Models;

public class Order
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonRequired]
    public string CustomerId { get; set; } = string.Empty;

    [BsonRequired]
    public List<OrderItem> Items { get; set; } = new();

    public string Status { get; set; } = OrderStatus.Pending;

    public decimal TotalAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class OrderItem
{
    [BsonRequired]
    public string ProductId { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    [BsonRequired]
    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }
}

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Shipped = "Shipped";
    public const string Delivered = "Delivered";
    public const string Cancelled = "Cancelled";
}
