using CommerceHub.API.Models;
using CommerceHub.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace CommerceHub.API.Controllers;

[ApiController]
[Route("api/products")]
[Produces("application/json")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(IProductService productService, ILogger<ProductsController> logger)
    {
        _productService = productService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/products/{id}
    /// Returns a product by ID. Returns 404 if not found.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<Product>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProduct(string id, CancellationToken ct)
    {
        var product = await _productService.GetProductByIdAsync(id, ct);

        if (product is null)
            return NotFound(ApiResponse<Product>.Fail($"Product {id} not found."));

        return Ok(ApiResponse<Product>.Ok(product));
    }

    /// <summary>
    /// PATCH /api/products/{id}/stock
    /// Atomically adjusts stock by the given delta.
    /// Positive delta = restock. Negative delta = manual deduction.
    /// Returns 400 if delta is zero (no-op).
    /// Returns 409 if the adjustment would result in negative stock.
    /// Returns 404 if the product does not exist.
    /// </summary>
    [HttpPatch("{id}/stock")]
    [ProducesResponseType(typeof(ApiResponse<Product>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AdjustStock(
        string id,
        [FromBody] StockAdjustmentRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (request.Delta == 0)
            return BadRequest(ApiResponse<Product>.Fail("Delta must be non-zero. A zero adjustment has no effect."));

        // Check existence first so we can return a meaningful 404 vs 409
        var existing = await _productService.GetProductByIdAsync(id, ct);
        if (existing is null)
            return NotFound(ApiResponse<Product>.Fail($"Product {id} not found."));

        var updated = await _productService.AdjustStockAsync(id, request.Delta, ct);

        if (updated is null)
        {
            return Conflict(ApiResponse<Product>.Fail(
                $"Stock adjustment of {request.Delta} would result in negative stock. " +
                $"Current stock: {existing.Stock}."));
        }

        _logger.LogInformation(
            "Stock for product {ProductId} adjusted by {Delta}. New stock: {Stock}",
            id, request.Delta, updated.Stock);

        return Ok(ApiResponse<Product>.Ok(updated, $"Stock adjusted by {request.Delta}."));
    }
}
