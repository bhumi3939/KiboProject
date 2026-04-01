using CommerceHub.API.Configuration;
using CommerceHub.API.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.API.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly IMongoCollection<Order> _orders;

    public OrderRepository(IOptions<MongoDbSettings> settings)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        var db = client.GetDatabase(settings.Value.DatabaseName);
        _orders = db.GetCollection<Order>(settings.Value.OrdersCollection);

        // Index on CustomerId for query performance
        var idx = Builders<Order>.IndexKeys.Ascending(o => o.CustomerId);
        _orders.Indexes.CreateOneAsync(new CreateIndexModel<Order>(idx));
    }

    public async Task<Order?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var filter = Builders<Order>.Filter.Eq(o => o.Id, id);
        return await _orders.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Order> CreateAsync(Order order, CancellationToken ct = default)
    {
        order.CreatedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;
        await _orders.InsertOneAsync(order, cancellationToken: ct);
        return order;
    }

    public async Task<Order?> UpdateAsync(string id, UpdateOrderRequest request, CancellationToken ct = default)
    {
        // Guard: never allow updating a Shipped order
        var existingFilter = Builders<Order>.Filter.And(
            Builders<Order>.Filter.Eq(o => o.Id, id),
            Builders<Order>.Filter.Ne(o => o.Status, OrderStatus.Shipped)
        );

        var updateDef = Builders<Order>.Update
            .Set(o => o.UpdatedAt, DateTime.UtcNow);

        if (request.Status is not null)
            updateDef = updateDef.Set(o => o.Status, request.Status);

        if (request.CustomerId is not null)
            updateDef = updateDef.Set(o => o.CustomerId, request.CustomerId);

        var options = new FindOneAndUpdateOptions<Order>
        {
            ReturnDocument = ReturnDocument.After
        };

        // Returns null if no document matched (either not found or is Shipped)
        return await _orders.FindOneAndUpdateAsync(existingFilter, updateDef, options, ct);
    }
}
