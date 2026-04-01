using CommerceHub.API.Models;

namespace CommerceHub.API.Services;

public interface IProductService
{
    Task<Product?> GetProductByIdAsync(string id, CancellationToken ct = default);
    Task<Product?> AdjustStockAsync(string id, int delta, CancellationToken ct = default);
}
