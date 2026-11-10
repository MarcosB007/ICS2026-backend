using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using FluentAssertions;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Pruebas de integración HTTP para los endpoints de ProductsController.
/// Se utiliza la API real levantada por WebApplicationFactory y SQLite en memoria.
/// </summary>
[Collection("IntegrationTests")]
public class ProductsControllerIntegrationTests
{
    private readonly HttpClient _client;

    public ProductsControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AddProduct_WhenUnauthenticated_ReturnsUnauthorized()
    {
        // Arrange: una solicitud válida, pero sin encabezado Authorization.
        var request = CreateProductRequest();

        // Act
        var response = await _client.PostAsJsonAsync("/api/products", request);

        // Assert: el endpoint requiere autenticación y el rol Admin.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AddProduct_WhenAuthenticatedAsRegularUser_ReturnsForbidden()
    {
        // Arrange: registrar un usuario normal para obtener un JWT real con rol USER.
        var token = await RegisterRegularUserAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var request = CreateProductRequest();

        // Act
        var response = await _client.PostAsJsonAsync("/api/products", request);

        // Assert: el usuario está autenticado, pero no tiene el rol Admin.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddProduct_WhenAuthenticatedAsAdmin_CreatesProductAndPersistsIt()
    {
        // Arrange: iniciar sesión con el administrador cargado desde admins.json.
        var token = await LoginAsAdminAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var request = CreateProductRequest();

        // Act: crear el producto mediante HTTP.
        var createResponse = await _client.PostAsJsonAsync("/api/products", request);

        // Assert: la API responde Created y proporciona la ubicación del recurso.
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        createResponse.Headers.Location.Should().NotBeNull();

        var createdProduct = await createResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        createdProduct.Should().NotBeNull();
        createdProduct!.Sku.Should().Be(request.Sku);
        createdProduct.Name.Should().Be(request.Name);

        // Comprobar la persistencia consultando el recurso desde otro request HTTP.
        var getResponse = await _client.GetAsync($"/api/products/{createdProduct.Id}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var persistedProduct = await getResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        persistedProduct.Should().NotBeNull();
        persistedProduct!.Id.Should().Be(createdProduct.Id);
        persistedProduct.Sku.Should().Be(request.Sku);
        persistedProduct.StockQuantity.Should().Be(request.StockQuantity);
    }

    [Fact]
    public async Task AddProduct_WhenSkuAlreadyExists_ReturnsBadRequestInsteadOfServerError()
    {
        // Arrange: crear primero un producto válido como administrador.
        var token = await LoginAsAdminAndGetTokenAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var request = CreateProductRequest();

        var firstResponse = await _client.PostAsJsonAsync("/api/products", request);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act: volver a enviar el mismo SKU.
        var duplicateResponse = await _client.PostAsJsonAsync("/api/products", request);

        // Assert: el middleware transforma DuplicatedEntityException en 400.
        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        duplicateResponse.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    private static ProductModel.Request CreateProductRequest()
    {
        var uniqueId = Guid.NewGuid().ToString("N");

        return new ProductModel.Request(
            Sku: $"SKU-TEST-{uniqueId}",
            InternalCode: $"INT-TEST-{uniqueId}",
            Name: $"Producto de integración {uniqueId[..8]}",
            Description: "Producto creado durante una prueba de integración",
            CurrentUnitPrice: 100m,
            StockQuantity: 10
        );
    }

    private async Task<string> LoginAsAdminAndGetTokenAsync()
    {
        // Estas credenciales corresponden al administrador definido en admins.json.
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginModel("admin", "Admin123!"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el administrador de prueba debe estar cargado desde admins.json");

        return await ReadTokenAsync(response);
    }

    private async Task<string> RegisterRegularUserAndGetTokenAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var request = new RegisterModel(
            Username: $"testuser{uniqueId}",
            Password: "Test123!",
            Email: $"testuser{uniqueId}@integration.local");

        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el usuario de prueba debe poder registrarse para obtener un JWT válido");

        return await ReadTokenAsync(response);
    }

    private static async Task<string> ReadTokenAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        // La API serializa normalmente como camelCase, pero se admite también PascalCase.
        var token = document.RootElement.TryGetProperty("token", out var camelCaseToken)
            ? camelCaseToken.GetString()
            : document.RootElement.TryGetProperty("Token", out var pascalCaseToken)
                ? pascalCaseToken.GetString()
                : null;

        token.Should().NotBeNullOrWhiteSpace("la respuesta de autenticación debe incluir un JWT");
        return token!;
    }
}
