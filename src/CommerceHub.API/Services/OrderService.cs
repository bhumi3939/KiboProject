using CommerceHub.API.Events;
using CommerceHub.API.Models;
using CommerceHub.API.Repositories;

namespace CommerceHub.API.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _orderRepo;
    private readonly IProductRepository _productRepo;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepo,
        IProductRepository productRepo,
        IMessagePublisher publisher,
        ILogger<OrderService> logger)
    {
        _orderRepo = orderRepo;
        _productRepo = productRepo;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<Order?> GetOrderByIdAsync(string id, CancellationToken ct = default)
    {
        return await _orderRepo.GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Checkout flow:
    /// 1. Validate all items (quantity >= 1 enforced by DTO, but re-checked here)
    /// 2. For each item, atomically decrement stock via FindOneAndUpdate with stock >= qty guard
    /// 3. If any item fails (out of stock), roll-back previously decremented items
    /// 4. Persist the Order document
    /// 5. Publish OrderCreated event to RabbitMQ
    /// </summary>
    public async Task<Order> CheckoutAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        // Validate quantities — belt-and-suspenders beyond DTO annotations
        foreach (var item in request.Items)
        {
            if (item.Quantity < 1)
                throw new ArgumentException($"Quantity for product {item.ProductId} must be at least 1.");
        }

        var decrementedItems = new List<(string productId, int quantity, decimal unitPrice, string name)>();

        try
        {
            // Phase 1: atomically reserve stock for every item
            foreach (var item in request.Items)
            {
                var updated = await _productRepo.DecrementStockAtomicAsync(item.ProductId, item.Quantity, ct);

                if (updated is null)
                {
                    // Either product not found or insufficient stock
                    throw new InvalidOperationException(
                        $"Product {item.ProductId} is out of stock or does not exist.");
                }

                decrementedItems.Add((item.ProductId, item.Quantity, updated.Price, updated.Name));
            }

            // Phase 2: build and persist the order
            var order = new Order
            {
                CustomerId = request.CustomerId,
                Status = OrderStatus.Pending,
                Items = decrementedItems.Select(d => new OrderItem
                {
                    ProductId = d.productId,
                    ProductName = d.name,
                    Quantity = d.quantity,
                    UnitPrice = d.unitPrice
                }).ToList()
            };

            order.TotalAmount = order.Items.Sum(i => i.Quantity * i.UnitPrice);

            var created = await _orderRepo.CreateAsync(order, ct);

            // Phase 3: publish event — fire and forget (non-critical path)
            var evt = new OrderCreatedEvent
            {
                OrderId = created.Id!,
                CustomerId = created.CustomerId,
                TotalAmount = created.TotalAmount,
                Items = created.Items.Select(i => new OrderCreatedItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList()
            };

            await _publisher.PublishAsync(evt, "order.created", ct);

            _logger.LogInformation("Order {OrderId} created for customer {CustomerId}", created.Id, created.CustomerId);
            return created;
        }
        catch
        {
            // Compensate: restore stock for items already decremented before the failure
            foreach (var (productId, quantity, _, _) in decrementedItems)
            {
                try
                {
                    await _productRepo.AdjustStockAtomicAsync(productId, quantity, ct);
                    _logger.LogWarning("Rolled back stock for product {ProductId} by {Qty}", productId, quantity);
                }
                catch (Exception rollbackEx)
                {
                    // Log but don't swallow original exception
                    _logger.LogError(rollbackEx, "Failed to roll back stock for product {ProductId}", productId);
                }
            }

            throw;
        }
    }

    public async Task<Order?> UpdateOrderAsync(string id, UpdateOrderRequest request, CancellationToken ct = default)
    {
        return await _orderRepo.UpdateAsync(id, request, ct);
    }
}
