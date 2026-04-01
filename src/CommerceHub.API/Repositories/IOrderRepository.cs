using CommerceHub.API.Models;

namespace CommerceHub.API.Repositories;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<Order> CreateAsync(Order order, CancellationToken ct = default);

    /// <summary>
    /// Idempotent update. Returns null if blocked (e.g. order is Shipped).
    /// </summary>
    Task<Order?> UpdateAsync(string id, UpdateOrderRequest request, CancellationToken ct = default);
}
