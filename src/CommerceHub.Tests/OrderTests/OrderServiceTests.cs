using CommerceHub.API.Events;
using CommerceHub.API.Models;
using CommerceHub.API.Repositories;
using CommerceHub.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace CommerceHub.Tests.OrderTests;

[TestFixture]
public class OrderServiceTests
{
    private Mock<IOrderRepository> _orderRepoMock = null!;
    private Mock<IProductRepository> _productRepoMock = null!;
    private Mock<IMessagePublisher> _publisherMock = null!;
    private OrderService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _orderRepoMock = new Mock<IOrderRepository>();
        _productRepoMock = new Mock<IProductRepository>();
        _publisherMock = new Mock<IMessagePublisher>();

        _sut = new OrderService(
            _orderRepoMock.Object,
            _productRepoMock.Object,
            _publisherMock.Object,
            NullLogger<OrderService>.Instance);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Test]
    public async Task Checkout_WithZeroQuantity_ThrowsArgumentException()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 0 }
            }
        };

        var act = async () => await _sut.CheckoutAsync(request);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Quantity*must be at least 1*");
    }

    [Test]
    public async Task Checkout_WithNegativeQuantity_ThrowsArgumentException()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = -5 }
            }
        };

        var act = async () => await _sut.CheckoutAsync(request);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Quantity*must be at least 1*");
    }

    [Test]
    public async Task Checkout_WithMultipleItems_OneHasZeroQuantity_ThrowsBeforeHittingDb()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 2 },
                new() { ProductId = "prod-2", Quantity = 0 }  // invalid
            }
        };

        var act = async () => await _sut.CheckoutAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();

        // Repo must never be touched when validation fails
        _productRepoMock.Verify(
            r => r.DecrementStockAtomicAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Stock decrement ───────────────────────────────────────────────────────

    [Test]
    public async Task Checkout_CallsDecrementStockAtomic_ForEachItem()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 3 },
                new() { ProductId = "prod-2", Quantity = 1 }
            }
        };

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-1", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-1", Name = "Widget", Price = 9.99m, Stock = 7 });

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-2", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-2", Name = "Gadget", Price = 4.99m, Stock = 9 });

        _orderRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order o, CancellationToken _) => { o.Id = "order-123"; return o; });

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(request);

        // Verify atomic decrement was called with exact quantities — not a read-modify-write
        _productRepoMock.Verify(
            r => r.DecrementStockAtomicAsync("prod-1", 3, It.IsAny<CancellationToken>()),
            Times.Once);

        _productRepoMock.Verify(
            r => r.DecrementStockAtomicAsync("prod-2", 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Checkout_WhenProductOutOfStock_ThrowsInvalidOperationException()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-out", Quantity = 10 }
            }
        };

        // Null means MongoDB filter didn't match (stock < qty or product missing)
        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-out", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var act = async () => await _sut.CheckoutAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*out of stock*");
    }

    [Test]
    public async Task Checkout_WhenSecondItemOutOfStock_RollsBackFirstItem()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 2 },
                new() { ProductId = "prod-2", Quantity = 99 }  // will fail
            }
        };

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-1", 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-1", Name = "Widget", Price = 5m, Stock = 8 });

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-2", 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);  // out of stock

        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync("prod-1", 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-1", Stock = 10 });

        var act = async () => await _sut.CheckoutAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Rollback must restore the first item's stock
        _productRepoMock.Verify(
            r => r.AdjustStockAtomicAsync("prod-1", 2, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Checkout_CalculatesTotalAmount_Correctly()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 2 },
                new() { ProductId = "prod-2", Quantity = 3 }
            }
        };

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-1", 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-1", Name = "Widget", Price = 10.00m, Stock = 8 });

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-2", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-2", Name = "Gadget", Price = 5.00m, Stock = 7 });

        Order? capturedOrder = null;
        _orderRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((o, _) => capturedOrder = o)
            .ReturnsAsync((Order o, CancellationToken _) => { o.Id = "order-abc"; return o; });

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(request);

        // 2 × 10.00 + 3 × 5.00 = 35.00
        capturedOrder.Should().NotBeNull();
        capturedOrder!.TotalAmount.Should().Be(35.00m);
    }

    // ── Event emission ────────────────────────────────────────────────────────

    [Test]
    public async Task Checkout_PublishesOrderCreatedEvent_AfterSuccessfulOrder()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-999",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 1 }
            }
        };

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-1", Name = "Widget", Price = 15m, Stock = 4 });

        _orderRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order o, CancellationToken _) => { o.Id = "order-evt-1"; return o; });

        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(request);

        // Event must be published exactly once with correct routing key
        _publisherMock.Verify(
            p => p.PublishAsync(
                It.Is<OrderCreatedEvent>(e =>
                    e.OrderId == "order-evt-1" &&
                    e.CustomerId == "cust-999"),
                "order.created",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Checkout_DoesNotPublishEvent_WhenOrderCreationFails()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-1", Quantity = 1 }
            }
        };

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-1", Name = "Widget", Price = 5m, Stock = 9 });

        _orderRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB write failure"));

        var act = async () => await _sut.CheckoutAsync(request);

        await act.Should().ThrowAsync<Exception>();

        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Checkout_PublishedEvent_ContainsCorrectItemDetails()
    {
        var request = new CheckoutRequest
        {
            CustomerId = "cust-42",
            Items = new List<CheckoutItem>
            {
                new() { ProductId = "prod-A", Quantity = 4 }
            }
        };

        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-A", 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = "prod-A", Name = "Alpha", Price = 25m, Stock = 6 });

        _orderRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order o, CancellationToken _) => { o.Id = "order-42"; return o; });

        OrderCreatedEvent? capturedEvent = null;
        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<OrderCreatedEvent, string, CancellationToken>((e, _, _) => capturedEvent = e)
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(request);

        capturedEvent.Should().NotBeNull();
        capturedEvent!.Items.Should().HaveCount(1);
        capturedEvent.Items[0].ProductId.Should().Be("prod-A");
        capturedEvent.Items[0].Quantity.Should().Be(4);
        capturedEvent.Items[0].UnitPrice.Should().Be(25m);
        capturedEvent.TotalAmount.Should().Be(100m);
    }

    // ── Get order ─────────────────────────────────────────────────────────────

    [Test]
    public async Task GetOrderById_ReturnsNull_WhenOrderDoesNotExist()
    {
        _orderRepoMock
            .Setup(r => r.GetByIdAsync("nonexistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        var result = await _sut.GetOrderByIdAsync("nonexistent");

        result.Should().BeNull();
    }

    // ── Update order ──────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateOrder_DelegatesToRepo_AndReturnsResult()
    {
        var updatedOrder = new Order
        {
            Id = "order-1",
            CustomerId = "cust-1",
            Status = OrderStatus.Processing
        };

        _orderRepoMock
            .Setup(r => r.UpdateAsync("order-1", It.IsAny<UpdateOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedOrder);

        var result = await _sut.UpdateOrderAsync("order-1", new UpdateOrderRequest { Status = "Processing" });

        result.Should().NotBeNull();
        result!.Status.Should().Be(OrderStatus.Processing);
    }

    [Test]
    public async Task UpdateOrder_ReturnsNull_WhenRepoReturnsNull()
    {
        // Repo returns null when the order is Shipped or doesn't exist
        _orderRepoMock
            .Setup(r => r.UpdateAsync("order-shipped", It.IsAny<UpdateOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        var result = await _sut.UpdateOrderAsync("order-shipped", new UpdateOrderRequest { Status = "Delivered" });

        result.Should().BeNull("repo returns null for Shipped or non-existent orders — service must propagate this to the controller");
    }

    [Test]
    public async Task UpdateOrder_NeverCallsCheckout_WhenUpdating()
    {
        // Regression guard: updating an order must not touch stock
        _orderRepoMock
            .Setup(r => r.UpdateAsync("order-1", It.IsAny<UpdateOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Order { Id = "order-1", Status = OrderStatus.Processing });

        await _sut.UpdateOrderAsync("order-1", new UpdateOrderRequest { Status = "Processing" });

        _productRepoMock.Verify(
            r => r.DecrementStockAtomicAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "updating an order must never touch product stock");
    }
}
