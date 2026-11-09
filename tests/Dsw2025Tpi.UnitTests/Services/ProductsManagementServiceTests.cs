using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Application.Exceptions;
using Dsw2025Tpi.Application.Services;
using Dsw2025Tpi.Domain.Entities;
using Dsw2025Tpi.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Linq.Expressions;

namespace Dsw2025Tpi.UnitTests.Services;

public class ProductsManagementServiceTests
{
    private readonly Mock<IRepository> _repositoryMock = new();
    private readonly ProductsManagementService _service;

    public ProductsManagementServiceTests()
    {
        _service = new ProductsManagementService(
            _repositoryMock.Object,
            NullLogger<ProductsManagementService>.Instance);
    }

    // ---------- AddProduct ----------

    [Fact]
    public async Task AddProduct_WithValidData_ReturnsProductAndCallsRepositoryAdd()
    {
        // Arrange
        var request = new ProductModel.Request(
            "SKU-001", "INT-001", "Mouse Gamer", "Mouse RGB", 1500m, 10);

        _repositoryMock
            .Setup(r => r.First<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync((Product?)null);   // no existe ningún producto con ese SKU

        _repositoryMock
            .Setup(r => r.Add(It.IsAny<Product>()))
            .ReturnsAsync((Product p) => p);

        // Act
        var result = await _service.AddProduct(request);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBeEmpty();
        result.Sku.Should().Be("SKU-001");
        result.InternalCode.Should().Be("INT-001");
        result.Name.Should().Be("Mouse Gamer");
        result.Description.Should().Be("Mouse RGB");
        result.CurrentUnitPrice.Should().Be(1500m);
        result.StockQuantity.Should().Be(10);
        result.IsActive.Should().BeTrue();

        _repositoryMock.Verify(
            r => r.Add(It.Is<Product>(p =>
                p.Sku == "SKU-001" &&
                p.Name == "Mouse Gamer" &&
                p.CurrentUnitPrice == 1500m &&
                p.StockQuantity == 10)),
            Times.Once);
    }

    [Fact]
    public async Task AddProduct_WithDuplicateSku_ThrowsDuplicatedEntityException()
    {
        // Arrange
        var request = new ProductModel.Request(
            "SKU-001", "INT-002", "Teclado", null, 800m, 5);

        var existing = new Product("SKU-001", "Otro producto", 100m, 3, null, "INT-001");

        _repositoryMock
            .Setup(r => r.First<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync(existing);         // simula que el SKU ya existe

        // Act
        Func<Task> act = () => _service.AddProduct(request);

        // Assert
        await act.Should().ThrowAsync<DuplicatedEntityException>();

        _repositoryMock.Verify(r => r.Add(It.IsAny<Product>()), Times.Never);
    }

    [Theory]
    [InlineData(100, 0, "stock")]     // stock igual a cero
    [InlineData(100, -5, "stock")]    // stock negativo
    [InlineData(-1, 10, "precio")]    // precio negativo
    public async Task AddProduct_WithInvalidStockOrPrice_ThrowsInvalidDataException(
        int price, int stock, string messageFragment)
    {
        // Arrange
        var request = new ProductModel.Request(
            "SKU-009", "INT-009", "Monitor", null, price, stock);

        // Act
        Func<Task> act = () => _service.AddProduct(request);

        // Assert
        await act.Should().ThrowAsync<InvalidDataException>()
                 .WithMessage($"*{messageFragment}*");

        // La validación ocurre ANTES de consultar o guardar en el repositorio
        _repositoryMock.Verify(
            r => r.First<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()),
            Times.Never);
        _repositoryMock.Verify(r => r.Add(It.IsAny<Product>()), Times.Never);
    }




    //Para pruebas de listado de productos

        // ---------- GetProducts ----------

    // Hace que el mock aplique el filtro que le envía el servicio sobre una lista de prueba
    private void SetupProducts(List<Product> products)
    {
        _repositoryMock
            .Setup(r => r.GetFiltered<Product>(It.IsAny<Expression<Func<Product, bool>>>(), It.IsAny<string[]>()))
            .ReturnsAsync((Expression<Func<Product, bool>> predicate, string[] include) =>
                products.Where(predicate.Compile()));
    }

    [Fact]
    public async Task GetProducts_WhenIncludeInactiveIsFalse_ReturnsOnlyActiveProducts()
    {
        // Arrange
        var activo1 = new Product("SKU-A1", "Auriculares", 500m, 5, null, "INT-A1");
        var activo2 = new Product("SKU-A2", "Parlante", 900m, 3, null, "INT-A2");
        var inactivo = new Product("SKU-I1", "Webcam", 700m, 2, null, "INT-I1") { IsActive = false };
        SetupProducts(new List<Product> { activo1, activo2, inactivo });

        var filter = new ProductModel.FilterProduct(null, null, null, null);

        // Act
        var result = await _service.GetProducts(filter, includeInactive: false);

        // Assert
        result.Total.Should().Be(2);
        result.ProductItems.Should().HaveCount(2);
        result.ProductItems.Should().OnlyContain(p => p.IsActive);
        result.ProductItems.Should().NotContain(p => p.Sku == "SKU-I1");
    }

    [Fact]
    public async Task GetProducts_WithPaginationFilter_AppliesSkipAndTakeCorrectly()
    {
        // Arrange: 15 productos, de "Producto 01" a "Producto 15"
        var products = Enumerable.Range(1, 15)
            .Select(i => new Product($"SKU-{i:00}", $"Producto {i:00}", 100m, 10, null, $"INT-{i:00}"))
            .ToList();
        SetupProducts(products);

        var filter = new ProductModel.FilterProduct(null, null, 2, 5);   // página 2, de a 5

        // Act
        var result = await _service.GetProducts(filter);

        // Assert
        result.Total.Should().Be(15);
        result.ProductItems.Should().HaveCount(5);
        result.ProductItems.Select(p => p.Name).Should().Equal(
            "Producto 06", "Producto 07", "Producto 08", "Producto 09", "Producto 10");
    }

    [Fact]
    public async Task GetProducts_WithSearchTerm_FiltersByNameSkuOrInternalCode()
    {
        // Arrange
        SetupProducts(new List<Product>
        {
            new("SKU-100", "Mouse Gamer", 2500m, 10, null, "INT-100"),
            new("SKU-200", "Teclado Mecanico", 4000m, 10, null, "INT-200"),
            new("SKU-300", "Monitor", 90000m, 10, null, "INT-300"),
        });

        var filter = new ProductModel.FilterProduct(null, "mouse", null, null);   // en minúsculas

        // Act
        var result = await _service.GetProducts(filter);

        // Assert
        result.Total.Should().Be(1);
        result.ProductItems.Should().ContainSingle()
              .Which.Name.Should().Be("Mouse Gamer");
    }

}