using CommerceHub.API.Models;
using CommerceHub.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace CommerceHub.API.Controllers;

[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(IOrderService orderService, ILogger<OrdersController> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/orders/{id}
    /// Returns 404 if the order does not exist.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<Order>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(string id, CancellationToken ct)
    {
        var order = await _orderService.GetOrderByIdAsync(id, ct);

        if (order is null)
            return NotFound(ApiResponse<Order>.Fail($"Order {id} not found."));

        return Ok(ApiResponse<Order>.Ok(order));
    }

    /// <summary>
    /// POST /api/orders/checkout
    /// Verifies stock, decrements inventory atomically, creates order, publishes event.
    /// </summary>
    [HttpPost("checkout")]
    [ProducesResponseType(typeof(ApiResponse<Order>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var order = await _orderService.CheckoutAsync(request, ct);
            return CreatedAtAction(nameof(GetOrder), new { id = order.Id },
                ApiResponse<Order>.Ok(order, "Order created successfully."));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<Order>.Fail(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Out of stock or product not found
            return Conflict(ApiResponse<Order>.Fail(ex.Message));
        }
    }

    /// <summary>
    /// PUT /api/orders/{id}
    /// Idempotent update. Returns 409 if the order is already Shipped.
    /// Returns 404 if the order does not exist.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ApiResponse<Order>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateOrder(string id, [FromBody] UpdateOrderRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // Check if order exists first so we can distinguish 404 vs 409
        var existing = await _orderService.GetOrderByIdAsync(id, ct);

        if (existing is null)
            return NotFound(ApiResponse<Order>.Fail($"Order {id} not found."));

        if (existing.Status == Models.OrderStatus.Shipped)
            return Conflict(ApiResponse<Order>.Fail("Cannot update an order that has already been shipped."));

        var updated = await _orderService.UpdateOrderAsync(id, request, ct);

        if (updated is null)
            return Conflict(ApiResponse<Order>.Fail("Order update was blocked — it may have been shipped concurrently."));

        return Ok(ApiResponse<Order>.Ok(updated, "Order updated successfully."));
    }
}
