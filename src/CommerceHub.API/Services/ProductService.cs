using CommerceHub.API.Models;
using CommerceHub.API.Repositories;

namespace CommerceHub.API.Services;

public class ProductService : IProductService
{
    private readonly IProductRepository _productRepo;

    public ProductService(IProductRepository productRepo)
    {
        _productRepo = productRepo;
    }

    public async Task<Product?> GetProductByIdAsync(string id, CancellationToken ct = default)
    {
        return await _productRepo.GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Delegates entirely to the atomic repo method.
    /// Returns null if product not found, or if negative delta would underflow stock.
    /// </summary>
    public async Task<Product?> AdjustStockAsync(string id, int delta, CancellationToken ct = default)
    {
        return await _productRepo.AdjustStockAtomicAsync(id, delta, ct);
    }
}
