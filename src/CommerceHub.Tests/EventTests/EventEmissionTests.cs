using CommerceHub.API.Events;
using CommerceHub.API.Models;
using CommerceHub.API.Repositories;
using CommerceHub.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace CommerceHub.Tests.EventTests;

/// <summary>
/// Focused tests on event emission contract.
/// Verifies that IMessagePublisher is called with the correct payload,
/// routing key, and only under the right conditions.
/// </summary>
[TestFixture]
public class EventEmissionTests
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

    private void SetupSuccessfulProduct(string productId, decimal price = 10m, int stock = 10)
    {
        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync(productId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Product { Id = productId, Name = $"Product-{productId}", Price = price, Stock = stock });
    }

    private void SetupSuccessfulOrderCreate(string orderId = "order-1")
    {
        _orderRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order o, CancellationToken _) => { o.Id = orderId; return o; });
    }

    // ── Routing key ───────────────────────────────────────────────────────────

    [Test]
    public async Task Checkout_PublishesEvent_WithCorrectRoutingKey()
    {
        SetupSuccessfulProduct("prod-1");
        SetupSuccessfulOrderCreate();
        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new() { new() { ProductId = "prod-1", Quantity = 1 } }
        });

        _publisherMock.Verify(
            p => p.PublishAsync(
                It.IsAny<OrderCreatedEvent>(),
                "order.created",    // must be this exact routing key
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Event payload integrity ───────────────────────────────────────────────

    [Test]
    public async Task Checkout_PublishedEvent_HasOrderId_MatchingCreatedOrder()
    {
        SetupSuccessfulProduct("prod-1", price: 20m);
        SetupSuccessfulOrderCreate("order-xyz");

        OrderCreatedEvent? captured = null;
        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<OrderCreatedEvent, string, CancellationToken>((e, _, _) => captured = e)
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new() { new() { ProductId = "prod-1", Quantity = 2 } }
        });

        captured.Should().NotBeNull();
        captured!.OrderId.Should().Be("order-xyz");
        captured.CustomerId.Should().Be("cust-1");
        captured.TotalAmount.Should().Be(40m);  // 2 × 20
    }

    [Test]
    public async Task Checkout_PublishedEvent_OccurredAt_IsRecentUtcTimestamp()
    {
        SetupSuccessfulProduct("prod-1");
        SetupSuccessfulOrderCreate();

        OrderCreatedEvent? captured = null;
        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<OrderCreatedEvent, string, CancellationToken>((e, _, _) => captured = e)
            .Returns(Task.CompletedTask);

        var before = DateTime.UtcNow;
        await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new() { new() { ProductId = "prod-1", Quantity = 1 } }
        });
        var after = DateTime.UtcNow;

        captured!.OccurredAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── Publish-once guarantee ────────────────────────────────────────────────

    [Test]
    public async Task Checkout_PublishesExactlyOneEvent_EvenWithMultipleItems()
    {
        SetupSuccessfulProduct("prod-1", price: 5m);
        SetupSuccessfulProduct("prod-2", price: 8m);
        SetupSuccessfulOrderCreate("order-multi");
        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new()
            {
                new() { ProductId = "prod-1", Quantity = 3 },
                new() { ProductId = "prod-2", Quantity = 2 }
            }
        });

        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "exactly one OrderCreated event per order — not one per item");
    }

    // ── No event on failure ───────────────────────────────────────────────────

    [Test]
    public async Task Checkout_DoesNotPublish_WhenStockInsufficient()
    {
        _productRepoMock
            .Setup(r => r.DecrementStockAtomicAsync("prod-1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var act = async () => await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new() { new() { ProductId = "prod-1", Quantity = 5 } }
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Checkout_DoesNotPublish_WhenValidationFails()
    {
        var act = async () => await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new() { new() { ProductId = "prod-1", Quantity = -1 } }
        });

        await act.Should().ThrowAsync<ArgumentException>();

        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Publisher failure is non-fatal ────────────────────────────────────────

    [Test]
    public async Task Checkout_WhenPublisherThrows_ExceptionPropagates()
    {
        // This test documents the current design decision:
        // publisher failure is NOT swallowed — the caller sees the exception.
        // If you want fire-and-forget, change this to Assert.DoesNotThrowAsync.
        SetupSuccessfulProduct("prod-1");
        SetupSuccessfulOrderCreate();
        _publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Broker unavailable"));

        var act = async () => await _sut.CheckoutAsync(new CheckoutRequest
        {
            CustomerId = "cust-1",
            Items = new() { new() { ProductId = "prod-1", Quantity = 1 } }
        });

        await act.Should().ThrowAsync<Exception>().WithMessage("*Broker unavailable*");
    }
}
