using CommerceHub.API.Models;
using CommerceHub.API.Repositories;
using CommerceHub.API.Services;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace CommerceHub.Tests.ProductTests;

[TestFixture]
public class ProductServiceTests
{
    private Mock<IProductRepository> _productRepoMock = null!;
    private ProductService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _productRepoMock = new Mock<IProductRepository>();
        _sut = new ProductService(_productRepoMock.Object);
    }

    // ── AdjustStock ───────────────────────────────────────────────────────────

    [Test]
    public async Task AdjustStock_PositiveDelta_CallsRepoWithCorrectArgs()
    {
        var productId = "prod-1";
        var expectedProduct = new Product { Id = productId, Stock = 20 };

        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync(productId, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedProduct);

        var result = await _sut.AdjustStockAsync(productId, 10);

        result.Should().NotBeNull();
        result!.Stock.Should().Be(20);
        _productRepoMock.Verify(
            r => r.AdjustStockAtomicAsync(productId, 10, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AdjustStock_NegativeDelta_WouldUnderflow_ReturnsNull()
    {
        // Repo returns null when MongoDB filter (stock >= |delta|) doesn't match
        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync("prod-1", -100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var result = await _sut.AdjustStockAsync("prod-1", -100);

        result.Should().BeNull("negative delta that would underflow stock must return null");
    }

    [Test]
    public async Task AdjustStock_NegativeDelta_WithSufficientStock_Succeeds()
    {
        var expectedProduct = new Product { Id = "prod-1", Stock = 5 };

        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync("prod-1", -5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedProduct);

        var result = await _sut.AdjustStockAsync("prod-1", -5);

        result.Should().NotBeNull();
        result!.Stock.Should().Be(5);
    }

    [Test]
    public async Task AdjustStock_ZeroDelta_PassesThroughToRepo()
    {
        var expectedProduct = new Product { Id = "prod-1", Stock = 10 };

        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync("prod-1", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedProduct);

        var result = await _sut.AdjustStockAsync("prod-1", 0);

        result.Should().NotBeNull();
        _productRepoMock.Verify(
            r => r.AdjustStockAtomicAsync("prod-1", 0, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AdjustStock_ProductNotFound_ReturnsNull()
    {
        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync("nonexistent", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var result = await _sut.AdjustStockAsync("nonexistent", 5);

        result.Should().BeNull();
    }

    // ── GetProduct ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetProductById_ReturnsProduct_WhenFound()
    {
        var expected = new Product { Id = "prod-1", Name = "Widget", Price = 9.99m, Stock = 50 };

        _productRepoMock
            .Setup(r => r.GetByIdAsync("prod-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _sut.GetProductByIdAsync("prod-1");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Widget");
        result.Price.Should().Be(9.99m);
    }

    [Test]
    public async Task GetProductById_ReturnsNull_WhenNotFound()
    {
        _productRepoMock
            .Setup(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var result = await _sut.GetProductByIdAsync("missing");

        result.Should().BeNull();
    }

    // ── Concurrency edge cases ────────────────────────────────────────────────

    [Test]
    public async Task AdjustStock_ConcurrentNegativeDeltas_RepoCalledEachTime()
    {
        // Simulates two concurrent -5 adjustments on stock of 8
        // First returns product, second returns null (stock exhausted)
        var callCount = 0;
        _productRepoMock
            .Setup(r => r.AdjustStockAtomicAsync("prod-1", -5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new Product { Id = "prod-1", Stock = 3 }
                    : (Product?)null;
            });

        var first = await _sut.AdjustStockAsync("prod-1", -5);
        var second = await _sut.AdjustStockAsync("prod-1", -5);

        first.Should().NotBeNull("first adjustment should succeed");
        second.Should().BeNull("second adjustment should fail — stock insufficient");
    }
}
