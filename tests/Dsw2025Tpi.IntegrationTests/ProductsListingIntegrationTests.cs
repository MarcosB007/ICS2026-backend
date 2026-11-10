using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using FluentAssertions;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Listado de productos: endpoint público (GET /api/products) y administrativo (GET /api/products/admin).
/// </summary>
[Collection("IntegrationTests")]
public class ProductsListingIntegrationTests
{
    private readonly CustomWebApplicationFactory _factory;

    public ProductsListingIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetProducts_PublicEndpoint_ReturnsOkWithPaginatedJsonStructure()
    {
        // Arrange: al menos un producto activo en la base, para no obtener 204.
        var product = await TestHelpers.SeedProductAsync(_factory);
        using var client = _factory.CreateClient(); // sin credenciales

        // Act
        var response = await client.GetAsync($"/api/products?search={Uri.EscapeDataString(product.Sku)}");

        // Assert: 200 OK y contrato JSON de paginación.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("productItems", out var items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);

        var result = document.RootElement.Deserialize<ProductModel.ResponsePagination>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        result.Should().NotBeNull();
        result!.Total.Should().Be(1);
        result.ProductItems.Should().ContainSingle(p => p.Id == product.Id);
    }

    [Fact]
    public async Task GetProducts_WithQueryStringFilters_ReturnsFilteredResultsFromSqlite()
    {
        // Arrange: 3 productos en SQLite, solo 2 contienen el término buscado.
        var term = $"Termino{Guid.NewGuid():N}";
        var match1 = await TestHelpers.SeedProductAsync(_factory, name: $"{term} Alfa");
        var match2 = await TestHelpers.SeedProductAsync(_factory, name: $"Beta {term}");
        var other = await TestHelpers.SeedProductAsync(_factory, name: $"Otro producto {Guid.NewGuid():N}");
        using var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/products?search={Uri.EscapeDataString(term)}&pageNumber=1&pageSize=10");

        // Assert: el model binding del query string filtra contra la base de datos.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>();
        result.Should().NotBeNull();
        result!.Total.Should().Be(2);
        result.ProductItems.Select(p => p.Id).Should().BeEquivalentTo(new[] { match1.Id, match2.Id });
        result.ProductItems.Select(p => p.Id).Should().NotContain(other.Id);
    }

    [Fact]
    public async Task GetAuthProducts_AdminEndpoint_WhenCalledByAdmin_IncludesInactiveProducts()
    {
        // Arrange: producto inactivo sembrado directamente en SQLite.
        var inactive = await TestHelpers.SeedProductAsync(_factory, isActive: false);
        using var adminClient = await TestHelpers.CreateAdminClientAsync(_factory);
        using var publicClient = _factory.CreateClient();
        var search = Uri.EscapeDataString(inactive.Sku);

        // Act
        var adminResponse = await adminClient.GetAsync($"/api/products/admin?search={search}");
        var publicResponse = await publicClient.GetAsync($"/api/products?search={search}");

        // Assert: el endpoint admin lo incluye...
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminResult = await adminResponse.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>();
        adminResult.Should().NotBeNull();
        adminResult!.ProductItems.Should().ContainSingle(p => p.Id == inactive.Id && p.IsActive == false);

        // ...a diferencia del público, que no lo muestra.
        publicResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
