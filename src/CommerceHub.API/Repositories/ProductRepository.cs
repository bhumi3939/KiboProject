using CommerceHub.API.Configuration;
using CommerceHub.API.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.API.Repositories;

public class ProductRepository : IProductRepository
{
    private readonly IMongoCollection<Product> _products;

    public ProductRepository(IOptions<MongoDbSettings> settings)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        var db = client.GetDatabase(settings.Value.DatabaseName);
        _products = db.GetCollection<Product>(settings.Value.ProductsCollection);
    }

    public async Task<Product?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var filter = Builders<Product>.Filter.Eq(p => p.Id, id);
        return await _products.Find(filter).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Single atomic FindOneAndUpdate:
    ///   filter = { _id, stock >= quantity }
    ///   update = { $inc: { stock: -quantity }, $set: { updatedAt } }
    /// If the filter doesn't match (product missing or insufficient stock),
    /// MongoDB returns null — no race condition possible.
    /// </summary>
    public async Task<Product?> DecrementStockAtomicAsync(string id, int quantity, CancellationToken ct = default)
    {
        var filter = Builders<Product>.Filter.And(
            Builders<Product>.Filter.Eq(p => p.Id, id),
            Builders<Product>.Filter.Gte(p => p.Stock, quantity)  // guard: stock >= qty
        );

        var update = Builders<Product>.Update
            .Inc(p => p.Stock, -quantity)
            .Set(p => p.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Product>
        {
            ReturnDocument = ReturnDocument.After
        };

        return await _products.FindOneAndUpdateAsync(filter, update, options, ct);
    }

    /// <summary>
    /// Atomic stock adjustment with underflow guard.
    /// For a negative delta, filter also requires stock >= |delta|.
    /// For a positive delta, no floor check needed.
    /// </summary>
    public async Task<Product?> AdjustStockAtomicAsync(string id, int delta, CancellationToken ct = default)
    {
        FilterDefinition<Product> filter;

        if (delta < 0)
        {
            // Negative adjustment: ensure stock won't go below zero
            filter = Builders<Product>.Filter.And(
                Builders<Product>.Filter.Eq(p => p.Id, id),
                Builders<Product>.Filter.Gte(p => p.Stock, Math.Abs(delta))
            );
        }
        else
        {
            filter = Builders<Product>.Filter.Eq(p => p.Id, id);
        }

        var update = Builders<Product>.Update
            .Inc(p => p.Stock, delta)
            .Set(p => p.UpdatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Product>
        {
            ReturnDocument = ReturnDocument.After
        };

        return await _products.FindOneAndUpdateAsync(filter, update, options, ct);
    }
}
