using CommerceHub.API.Models;

namespace CommerceHub.API.Repositories;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Atomically decrements stock by <paramref name="quantity"/> only if stock >= quantity.
    /// Returns the updated product, or null if insufficient stock.
    /// This is a single FindOneAndUpdate — safe under concurrent load.
    /// </summary>
    Task<Product?> DecrementStockAtomicAsync(string id, int quantity, CancellationToken ct = default);

    /// <summary>
    /// Atomically applies a delta to stock. Blocked if result would be negative.
    /// Returns the updated product, or null if the update would underflow.
    /// </summary>
    Task<Product?> AdjustStockAtomicAsync(string id, int delta, CancellationToken ct = default);
}
