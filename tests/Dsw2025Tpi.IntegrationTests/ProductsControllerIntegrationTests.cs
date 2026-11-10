using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dsw2025Tpi.Application.Dtos;
using FluentAssertions;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Creación de productos (POST /api/products): seguridad, persistencia real en SQLite y conflicto de SKU.
/// </summary>
[Collection("IntegrationTests")]
public class ProductsControllerIntegrationTests
{
    private readonly CustomWebApplicationFactory _factory;

    public ProductsControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AddProduct_WhenUnauthenticatedOrNotAdmin_ReturnsUnauthorizedOrForbidden()
    {
        // Arrange
        var request = TestHelpers.NewProductRequest();

        // Act 1: petición anónima (sin token) -> 401
        using var anonymousClient = _factory.CreateClient();
        var anonymousResponse = await anonymousClient.PostAsJsonAsync("/api/products", request);

        // Act 2: usuario autenticado con rol distinto de Admin -> 403
        var (userClient, _) = await TestHelpers.CreateCustomerClientAsync(_factory);
        using var userClientScope = userClient;
        var userResponse = await userClient.PostAsJsonAsync("/api/products", request);

        // Assert
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        userResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Ninguna de las dos peticiones rechazadas debe haber persistido el producto.
        (await TestHelpers.GetProductFromDbBySkuAsync(_factory, request.Sku)).Should().BeNull();
    }

    [Fact]
    public async Task AddProduct_WhenAuthenticatedAsAdmin_PersistsInSqliteAndReturnsCreated()
    {
        // Arrange
        using var adminClient = await TestHelpers.CreateAdminClientAsync(_factory);
        var request = TestHelpers.NewProductRequest("ADMIN", stock: 15);

        // Act
        var response = await adminClient.PostAsJsonAsync("/api/products", request);

        // Assert: respuesta HTTP
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        created.Should().NotBeNull();

        // Assert: la fila existe realmente en la tabla Products de SQLite, con los valores enviados.
        var persisted = await TestHelpers.GetProductFromDbAsync(_factory, created!.Id);
        persisted.Should().NotBeNull("la API debe haber insertado la fila en la base de datos");
        persisted!.Sku.Should().Be(request.Sku);
        persisted.InternalCode.Should().Be(request.InternalCode);
        persisted.Name.Should().Be(request.Name);
        persisted.Description.Should().Be(request.Description);
        persisted.CurrentUnitPrice.Should().Be(request.CurrentUnitPrice);
        persisted.StockQuantity.Should().Be(request.StockQuantity);
        persisted.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task AddProduct_WhenSkuAlreadyExistsInDatabase_ReturnsConflictStatusCode()
    {
        // Arrange: producto pre-insertado directamente en SQLite.
        var existing = await TestHelpers.SeedProductAsync(_factory);
        using var adminClient = await TestHelpers.CreateAdminClientAsync(_factory);

        var duplicated = new ProductModel.Request(
            Sku: existing.Sku,
            InternalCode: $"INT-DUP-{Guid.NewGuid():N}",
            Name: "Producto con SKU duplicado",
            Description: "Intento de crear un producto con un SKU ya existente",
            CurrentUnitPrice: 100m,
            StockQuantity: 5);

        // Act
        var response = await adminClient.PostAsJsonAsync("/api/products", duplicated);

        // Assert: no es un 500; el middleware responde 409 Conflict (o 400 Bad Request).
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest);

        // Y sigue existiendo un único producto con ese SKU.
        var persisted = await TestHelpers.GetProductFromDbBySkuAsync(_factory, existing.Sku);
        persisted.Should().NotBeNull();
        persisted!.Id.Should().Be(existing.Id);
    }
}
