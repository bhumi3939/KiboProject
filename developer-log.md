# Developer Log — AI-Augmented Development

## 1. AI Strategy

### Context provided to the AI

Before generating any code, I provided Claude with the complete project specification including:

- The four exact API endpoint routes, HTTP methods, and business rules
- MongoDB collection schemas for `Orders` and `Products`
- The concurrency requirement specifying `FindOneAndUpdate` with atomic filter guards
- The RabbitMQ topology: durable topic exchange, `order.created` routing key, persistent messages
- The three mandatory nUnit coverage areas
- The Docker Compose health check requirement (`depends_on` with `condition: service_healthy`)

I structured the context as explicit rules rather than vague descriptions. For example, I specified: *"Stock decrement must be a single `FindOneAndUpdate` with filter `{ _id, stock >= quantity }`. A read followed by a write is not acceptable — this is a race condition."* This level of precision steered the AI away from naive implementations.

### Schema rules shared

```
Orders collection:
  - _id: ObjectId
  - customerId: string (required)
  - items: [{ productId, productName, quantity, unitPrice }]
  - status: enum [Pending, Processing, Shipped, Delivered, Cancelled]
  - totalAmount: decimal
  - createdAt / updatedAt: DateTime UTC

Products collection:
  - _id: ObjectId
  - name, description: string
  - price: decimal
  - stock: int (never negative — enforced at DB layer)
  - createdAt / updatedAt: DateTime UTC
```

I also provided the interface contracts (`IOrderRepository`, `IProductRepository`, `IMessagePublisher`) before asking the AI to generate implementations, which kept the generated code aligned to the testable interface boundary.

---

## 2. Human Audit — Three Specific Corrections

### Correction 1 — Stock decrement was read-then-write (race condition)

**What the AI initially generated:**

```csharp
// AI's first attempt — UNSAFE under concurrent load
var product = await _products.FindAsync(filter).FirstOrDefaultAsync();
if (product.Stock < quantity)
    throw new InvalidOperationException("Out of stock");

product.Stock -= quantity;
await _products.ReplaceOneAsync(filter, product);
```

**Why this is wrong:** Between the `FindAsync` and the `ReplaceOneAsync`, another concurrent request could read the same stock value. Both would pass the `< quantity` check and both would decrement, producing negative stock. This is a classic TOCTOU (time-of-check-time-of-use) race condition.

**My correction:**

```csharp
// Single atomic FindOneAndUpdate — filter includes stock >= quantity guard
var filter = Builders<Product>.Filter.And(
    Builders<Product>.Filter.Eq(p => p.Id, id),
    Builders<Product>.Filter.Gte(p => p.Stock, quantity)
);

var update = Builders<Product>.Update
    .Inc(p => p.Stock, -quantity)
    .Set(p => p.UpdatedAt, DateTime.UtcNow);

return await _products.FindOneAndUpdateAsync(filter, update, options, ct);
```

MongoDB executes this as a single atomic document-level operation. If the filter doesn't match (insufficient stock), the method returns `null` — no update occurs. No race condition is possible.

---

### Correction 2 — `IMessagePublisher` was not abstracted (untestable)

**What the AI initially generated:**

The AI wired `RabbitMqPublisher` directly into `OrderService` via constructor injection of the concrete type:

```csharp
public OrderService(
    IOrderRepository orderRepo,
    IProductRepository productRepo,
    RabbitMqPublisher publisher)   // concrete — not mockable
```

**Why this is wrong:** nUnit tests for event emission would require a live RabbitMQ connection. Any CI/CD environment without a broker would fail. The spec explicitly requires mocking the publisher in tests.

**My correction:** I introduced `IMessagePublisher` as a clean interface:

```csharp
public interface IMessagePublisher
{
    Task PublishAsync<T>(T message, string routingKey, CancellationToken ct = default);
}
```

`OrderService` now depends on `IMessagePublisher`. `RabbitMqPublisher` is the registered implementation. In tests, `Mock<IMessagePublisher>` is injected, and `_publisherMock.Verify(...)` confirms the event was published with the correct payload and routing key — no broker required.

---

### Correction 3 — `PUT /api/orders/{id}` did not block Shipped orders at the repository level

**What the AI initially generated:**

The AI placed the Shipped guard only in the controller:

```csharp
// Controller only — repo would still execute the update if called directly
if (existing.Status == "Shipped")
    return Conflict(...);

await _orderService.UpdateOrderAsync(id, request);
```

**Why this is wrong:** The guard is only enforced at the HTTP boundary. If `UpdateAsync` is ever called from another service, background job, or test context, there is no protection. Business invariants belong at the data access layer.

**My correction:** I moved the guard into `OrderRepository.UpdateAsync` using a compound MongoDB filter:

```csharp
var existingFilter = Builders<Order>.Filter.And(
    Builders<Order>.Filter.Eq(o => o.Id, id),
    Builders<Order>.Filter.Ne(o => o.Status, OrderStatus.Shipped)
);
```

`FindOneAndUpdateAsync` only succeeds if the order exists **and** is not Shipped. If it returns `null`, the caller knows either the order doesn't exist or it was blocked. The controller then performs a separate existence check to distinguish 404 from 409 — giving accurate HTTP status codes while keeping the invariant enforced at the repo layer.

---

## 3. AI-Assisted Test Generation for Edge Cases

After writing the core service logic, I prompted Claude with the following:

> *"Given this `CheckoutAsync` implementation with a compensation loop for stock rollback, generate nUnit edge case tests covering: (1) the second item failing causing rollback of the first; (2) rollback itself failing without swallowing the original exception; (3) the published event payload containing correct item-level unit prices and total amount."*

The AI generated the `Checkout_WhenSecondItemOutOfStock_RollsBackFirstItem` test and the `Checkout_PublishedEvent_ContainsCorrectItemDetails` test. I refined both:

- The rollback test was missing a `Mock.Setup` for `AdjustStockAtomicAsync` on the rollback path, which would have caused the test to pass vacuously (the method call would return the default `null` without asserting anything meaningful). I added the explicit setup and the `Verify(Times.Once)` assertion.
- The payload test used `It.IsAny<OrderCreatedEvent>()` in the `Verify` call, which would pass even with a completely wrong payload. I replaced it with a `Callback` that captures the event, then uses FluentAssertions to verify each field individually.

The AI also surfaced the `Checkout_WhenPublisherThrows_ExceptionPropagates` test as a design question — it correctly identified that the current implementation does not swallow publisher exceptions and wrote a test documenting that behavior with a comment explaining it is a conscious design decision that can be changed to fire-and-forget if required.
