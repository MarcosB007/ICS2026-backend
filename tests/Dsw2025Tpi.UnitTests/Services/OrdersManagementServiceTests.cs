using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Application.Exceptions;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Dsw2025Tpi.UnitTests.Services;

public class OrdersManagementServiceTests
{
    private readonly Mock<IRepository> _repositoryMock = new();
    private readonly OrdersManagementService _service;

    public OrdersManagementServiceTests()
    {
        _service = new OrdersManagementService(
            _repositoryMock.Object,
            NullLogger<OrdersManagementService>.Instance);
    }

    [Fact]
    public async Task CreateOrder_WithValidItemsAndStock_CalculatesTotalAndDecrementsProductStock()
    {
        // Arrange
        var customer = new Customer { Name = "Juan Perez", Email = "juan@test.com" };
        var mouse = new Product("SKU-1", "Mouse", 1000m, 10, null, "INT-1");
        var cable = new Product("SKU-2", "Cable HDMI", 250.50m, 5, null, "INT-2");

        _repositoryMock.Setup(r => r.GetById<Customer>(customer.Id, It.IsAny<string[]>()))
                       .ReturnsAsync(customer);
        _repositoryMock.Setup(r => r.GetById<Product>(mouse.Id, It.IsAny<string[]>()))
                       .ReturnsAsync(mouse);
        _repositoryMock.Setup(r => r.GetById<Product>(cable.Id, It.IsAny<string[]>()))
                       .ReturnsAsync(cable);
        _repositoryMock.Setup(r => r.Update(It.IsAny<Product>()))
                       .ReturnsAsync((Product p) => p);
        _repositoryMock.Setup(r => r.Add(It.IsAny<Order>()))
                       .ReturnsAsync((Order o) => o);

        var request = new OrderRequest(
            customer.Id,
            "Calle Falsa 123",
            "Calle Falsa 123",
            null,
            new List<OrderItemRequest>
            {
                new(mouse.Id, 3),   // 3 x 1000,00 = 3000,00
                new(cable.Id, 2)    // 2 x  250,50 =  501,00
            });

        // Act
        var result = await _service.CreateOrder(request);

        // Assert: total = suma exacta de los subtotales
        result.TotalAmount.Should().Be(3501.00m);
        result.Items.Should().HaveCount(2);
        result.ClientName.Should().Be("Juan Perez");

        // Assert: el stock se descontó
        mouse.StockQuantity.Should().Be(7);
        cable.StockQuantity.Should().Be(3);

        // Assert: se actualizó cada producto y se guardó la orden una vez
        _repositoryMock.Verify(r => r.Update(mouse), Times.Once);
        _repositoryMock.Verify(r => r.Update(cable), Times.Once);
        _repositoryMock.Verify(r => r.Add(It.Is<Order>(o =>
                o.CustomerId == customer.Id &&
                o.TotalAmount == 3501.00m &&
                o.OrderItems != null && o.OrderItems.Count == 2)),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrder_WhenProductStockIsInsufficient_ThrowsInsufficientStockException()
    {
        // Arrange
        var customer = new Customer { Name = "Ana Gomez", Email = "ana@test.com" };
        var monitor = new Product("SKU-3", "Monitor", 90000m, 2, null, "INT-3");   // stock = 2

        _repositoryMock.Setup(r => r.GetById<Customer>(customer.Id, It.IsAny<string[]>()))
                       .ReturnsAsync(customer);
        _repositoryMock.Setup(r => r.GetById<Product>(monitor.Id, It.IsAny<string[]>()))
                       .ReturnsAsync(monitor);

        var request = new OrderRequest(
            customer.Id, "Av. Siempre Viva 742", "Av. Siempre Viva 742", null,
            new List<OrderItemRequest> { new(monitor.Id, 5) });   // pide 5

        // Act
        Func<Task> act = () => _service.CreateOrder(request);

        // Assert
        await act.Should().ThrowAsync<InsufficientStockException>();

        monitor.StockQuantity.Should().Be(2);   // el stock no cambió
        _repositoryMock.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        _repositoryMock.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrder_WhenCustomerDoesNotExist_ThrowsCustomerNotFoundException()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetById<Customer>(It.IsAny<Guid>(), It.IsAny<string[]>()))
                       .ReturnsAsync((Customer?)null);   // el cliente no existe

        var request = new OrderRequest(
            Guid.NewGuid(), "Calle 1", "Calle 1", null,
            new List<OrderItemRequest> { new(Guid.NewGuid(), 1) });

        // Act
        Func<Task> act = () => _service.CreateOrder(request);

        // Assert
        await act.Should().ThrowAsync<CustomerNotFoundException>();

        // No se consultaron ni modificaron productos, ni se creó la orden
        _repositoryMock.Verify(r => r.GetById<Product>(It.IsAny<Guid>(), It.IsAny<string[]>()), Times.Never);
        _repositoryMock.Verify(r => r.Update(It.IsAny<Product>()), Times.Never);
        _repositoryMock.Verify(r => r.Add(It.IsAny<Order>()), Times.Never);
    }
}