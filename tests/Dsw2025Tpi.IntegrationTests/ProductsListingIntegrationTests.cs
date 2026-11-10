using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using FluentAssertions;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Pruebas de integración HTTP para los endpoints de listado de productos.
/// Verifican acceso público, búsqueda, paginación y consulta administrativa
/// de productos inactivos mediante la API real y SQLite en memoria.
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
    public async Task GetProducts_WhenAnonymousAndMatchingProductExists_ReturnsProductsAndTotal()
    {
        // Arrange: crear un producto como administrador para consultarlo sin autenticación.
        using var adminClient = _factory.CreateClient();
        await AuthenticateAsAdminAsync(adminClient);
        var productRequest = CreateProductRequest("PUBLIC");
        var createResponse = await adminClient.PostAsJsonAsync("/api/products", productRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var publicClient = _factory.CreateClient();

        // Act: el endpoint público se consulta sin enviar un JWT.
        var response = await publicClient.GetAsync($"/api/products?search={Uri.EscapeDataString(productRequest.Sku)}");

        // Assert: debe devolver el producto y el total de coincidencias.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>();
        result.Should().NotBeNull();
        result!.Total.Should().Be(1);
        result.ProductItems.Should().ContainSingle(product => product.Sku == productRequest.Sku);
    }

    [Fact]
    public async Task GetProducts_WithSearchAndPagination_ReturnsExpectedPageAndTotal()
    {
        // Arrange: crear tres productos que compartan un identificador de búsqueda.
        using var adminClient = _factory.CreateClient();
        await AuthenticateAsAdminAsync(adminClient);
        var searchTerm = $"PAGINATION-{Guid.NewGuid():N}";

        for (var index = 1; index <= 3; index++)
        {
            var request = CreateProductRequest("PAGE", $"{searchTerm}-{index}");
            var createResponse = await adminClient.PostAsJsonAsync("/api/products", request);
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var client = _factory.CreateClient();

        // Act: consultar la primera página, con dos elementos por página.
        var firstPageResponse = await client.GetAsync(
            $"/api/products?search={Uri.EscapeDataString(searchTerm)}&pageNumber=1&pageSize=2");

        // Assert: el total representa todas las coincidencias, no solo los elementos de la página.
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPage = await firstPageResponse.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>();
        firstPage.Should().NotBeNull();
        firstPage!.Total.Should().Be(3);
        firstPage.ProductItems.Should().HaveCount(2);

        // Act: consultar la segunda página.
        var secondPageResponse = await client.GetAsync(
            $"/api/products?search={Uri.EscapeDataString(searchTerm)}&pageNumber=2&pageSize=2");

        // Assert: queda un solo elemento y el total sigue siendo tres.
        secondPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondPage = await secondPageResponse.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>();
        secondPage.Should().NotBeNull();
        secondPage!.Total.Should().Be(3);
        secondPage.ProductItems.Should().ContainSingle();

        firstPage.ProductItems.Select(product => product.Id)
            .Intersect(secondPage.ProductItems.Select(product => product.Id))
            .Should().BeEmpty("un mismo producto no debe aparecer en dos páginas distintas");
    }

    [Fact]
    public async Task GetAdminProducts_WhenAnonymousOrRegularUser_ReturnsUnauthorizedOrForbidden()
    {
        // Arrange
        using var anonymousClient = _factory.CreateClient();

        // Act / Assert: el endpoint administrativo no permite acceso anónimo.
        var anonymousResponse = await anonymousClient.GetAsync("/api/products/admin");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Arrange: registrar un usuario normal para obtener un JWT con rol USER.
        using var userClient = _factory.CreateClient();
        var userToken = await RegisterUserAndGetTokenAsync(userClient);
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // Act / Assert: estar autenticado no alcanza; se requiere el rol Admin.
        var userResponse = await userClient.GetAsync("/api/products/admin");
        userResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAdminProducts_IncludesInactiveProducts_WhilePublicListExcludesThem()
    {
        // Arrange: crear un producto y deshabilitarlo mediante el endpoint administrativo.
        using var adminClient = _factory.CreateClient();
        await AuthenticateAsAdminAsync(adminClient);

        var productRequest = CreateProductRequest("INACTIVE");
        var createResponse = await adminClient.PostAsJsonAsync("/api/products", productRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdProduct = await createResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        createdProduct.Should().NotBeNull();

        var disableResponse = await adminClient.PatchAsync($"/api/products/{createdProduct!.Id}", content: null);
        disableResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Act: consultar el mismo producto desde el listado público y el administrativo.
        using var publicClient = _factory.CreateClient();
        var publicResponse = await publicClient.GetAsync(
            $"/api/products?search={Uri.EscapeDataString(productRequest.Sku)}");

        var adminResponse = await adminClient.GetAsync(
            $"/api/products/admin?search={Uri.EscapeDataString(productRequest.Sku)}");

        // Assert: el listado público oculta inactivos y el administrativo los incluye.
        publicResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var adminResult = await adminResponse.Content.ReadFromJsonAsync<ProductModel.ResponsePagination>();
        adminResult.Should().NotBeNull();
        adminResult!.Total.Should().Be(1);
        adminResult.ProductItems.Should().ContainSingle(product =>
            product.Id == createdProduct.Id && product.IsActive == false);
    }

    private static ProductModel.Request CreateProductRequest(string prefix, string? name = null)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        return new ProductModel.Request(
            Sku: $"SKU-{prefix}-{uniqueId}",
            InternalCode: $"INT-{prefix}-{uniqueId}",
            Name: name ?? $"Producto {prefix} {uniqueId[..8]}",
            Description: "Producto creado para una prueba de integración de listados",
            CurrentUnitPrice: 150m,
            StockQuantity: 10);
    }

    private static async Task AuthenticateAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginModel("admin", "Admin123!"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el administrador de prueba debe estar cargado desde admins.json");

        var token = await ReadStringPropertyAsync(response, "token", "Token");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> RegisterUserAndGetTokenAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterModel(
                Username: $"listinguser{uniqueId}",
                Password: "Test123!",
                Email: $"listinguser{uniqueId}@integration.local"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el usuario de prueba debe poder registrarse para obtener un JWT válido");

        return await ReadStringPropertyAsync(response, "token", "Token");
    }

    private static async Task<string> ReadStringPropertyAsync(HttpResponseMessage response, params string[] names)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        foreach (var name in names)
        {
            if (document.RootElement.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"No se encontró ninguna de estas propiedades en la respuesta: {string.Join(", ", names)}");
    }
}
