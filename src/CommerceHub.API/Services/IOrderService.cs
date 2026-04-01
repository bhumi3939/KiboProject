using CommerceHub.API.Models;

namespace CommerceHub.API.Services;

public interface IOrderService
{
    Task<Order?> GetOrderByIdAsync(string id, CancellationToken ct = default);
    Task<Order> CheckoutAsync(CheckoutRequest request, CancellationToken ct = default);
    Task<Order?> UpdateOrderAsync(string id, UpdateOrderRequest request, CancellationToken ct = default);
}
