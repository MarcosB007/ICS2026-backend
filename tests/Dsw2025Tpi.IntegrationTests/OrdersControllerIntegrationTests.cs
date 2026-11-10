using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using FluentAssertions;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Pruebas de integración HTTP para la creación de órdenes.
/// Verifican autenticación, persistencia y actualización del stock usando la API real.
/// </summary>
[Collection("IntegrationTests")]
public class OrdersControllerIntegrationTests
{
    private readonly CustomWebApplicationFactory _factory;

    public OrdersControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateOrder_WhenUnauthenticated_ReturnsUnauthorized()
    {
        // Arrange
        using var client = _factory.CreateClient();
        var request = new
        {
            CustomerId = Guid.NewGuid(),
            ShippingAddress = "Calle de prueba 123",
            BillingAddress = "Calle de prueba 123",
            Notes = "Orden sin autenticación",
            Items = new[] { new { ProductId = Guid.NewGuid(), Quantity = 1 } }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/orders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateOrder_WhenAuthenticated_CreatesOrderPersistsItAndDecreasesStock()
    {
        // Arrange: crear un producto con stock inicial 10 y registrar un cliente real.
        using var adminClient = _factory.CreateClient();
        var adminToken = await LoginAsAdminAndGetTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var productRequest = CreateProductRequest(stock: 10);
        var productResponse = await adminClient.PostAsJsonAsync("/api/products", productRequest);
        productResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var product = await productResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        product.Should().NotBeNull();

        using var customerClient = _factory.CreateClient();
        var (customerToken, customerId) = await RegisterCustomerAndGetCredentialsAsync(customerClient);
        customerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

        const int orderedQuantity = 3;
        var orderRequest = new
        {
            CustomerId = customerId,
            ShippingAddress = "Av. Siempre Viva 742",
            BillingAddress = "Av. Siempre Viva 742",
            Notes = "Prueba de integración de órdenes",
            Items = new[] { new { ProductId = product!.Id, Quantity = orderedQuantity } }
        };

        // Act: crear la orden mediante HTTP.
        var createResponse = await customerClient.PostAsJsonAsync("/api/orders", orderRequest);

        // Assert: respuesta HTTP y encabezado Location.
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        createResponse.Headers.Location.Should().NotBeNull();

        using var createdDocument = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
        var orderId = ReadGuid(createdDocument.RootElement, "orderId", "OrderId");
        orderId.Should().NotBe(Guid.Empty);

        // Verificar persistencia consultando la orden desde su endpoint GET.
        var getOrderResponse = await customerClient.GetAsync($"/api/orders/{orderId}");
        getOrderResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var persistedOrderDocument = await JsonDocument.ParseAsync(await getOrderResponse.Content.ReadAsStreamAsync());
        ReadGuid(persistedOrderDocument.RootElement, "orderId", "OrderId").Should().Be(orderId);

        // Verificar que el stock se haya actualizado de 10 a 7.
        var getProductResponse = await customerClient.GetAsync($"/api/products/{product.Id}");
        getProductResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedProduct = await getProductResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        updatedProduct.Should().NotBeNull();
        updatedProduct!.StockQuantity.Should().Be(10 - orderedQuantity);
    }

    [Fact]
    public async Task CreateOrder_WhenStockIsInsufficient_ReturnsBadRequestAndDoesNotChangeStock()
    {
        // Arrange: el producto tiene 2 unidades, pero se solicitan 3.
        using var adminClient = _factory.CreateClient();
        var adminToken = await LoginAsAdminAndGetTokenAsync(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var productRequest = CreateProductRequest(stock: 2);
        var productResponse = await adminClient.PostAsJsonAsync("/api/products", productRequest);
        productResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var product = await productResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        product.Should().NotBeNull();

        using var customerClient = _factory.CreateClient();
        var (customerToken, customerId) = await RegisterCustomerAndGetCredentialsAsync(customerClient);
        customerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

        var orderRequest = new
        {
            CustomerId = customerId,
            ShippingAddress = "Calle de prueba 456",
            BillingAddress = "Calle de prueba 456",
            Notes = "Prueba de stock insuficiente",
            Items = new[] { new { ProductId = product!.Id, Quantity = 3 } }
        };

        // Registrar cuántas órdenes existen antes del intento para comprobar que no se persista ninguna.
        var ordersBeforeResponse = await customerClient.GetAsync("/api/orders");
        ordersBeforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ordersBeforeDocument = await JsonDocument.ParseAsync(await ordersBeforeResponse.Content.ReadAsStreamAsync());
        var ordersBefore = ReadInt(ordersBeforeDocument.RootElement, "total", "Total");

        // Act
        var response = await customerClient.PostAsJsonAsync("/api/orders", orderRequest);

        // Assert: la API debe rechazar la orden por falta de stock.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);

        // El producto debe conservar su stock original.
        var getProductResponse = await customerClient.GetAsync($"/api/products/{product.Id}");
        getProductResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var unchangedProduct = await getProductResponse.Content.ReadFromJsonAsync<ProductModel.ResponseProduct>();
        unchangedProduct.Should().NotBeNull();
        unchangedProduct!.StockQuantity.Should().Be(2);

        // No debe haberse persistido una orden nueva.
        var ordersAfterResponse = await customerClient.GetAsync("/api/orders");
        ordersAfterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ordersAfterDocument = await JsonDocument.ParseAsync(await ordersAfterResponse.Content.ReadAsStreamAsync());
        ReadInt(ordersAfterDocument.RootElement, "total", "Total").Should().Be(ordersBefore);
    }

    private static ProductModel.Request CreateProductRequest(int stock)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        return new ProductModel.Request(
            Sku: $"SKU-ORDER-{uniqueId}",
            InternalCode: $"INT-ORDER-{uniqueId}",
            Name: $"Producto para orden {uniqueId[..8]}",
            Description: "Producto creado para una prueba de integración de órdenes",
            CurrentUnitPrice: 250m,
            StockQuantity: stock);
    }

    private static async Task<string> LoginAsAdminAndGetTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginModel("admin", "Admin123!"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el administrador de prueba debe estar cargado desde admins.json");
        return await ReadStringPropertyAsync(response, "token", "Token");
    }

    private static async Task<(string Token, Guid CustomerId)> RegisterCustomerAndGetCredentialsAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterModel(
                Username: $"orderuser{uniqueId}",
                Password: "Test123!",
                Email: $"orderuser{uniqueId}@integration.local"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el cliente de prueba debe poder registrarse");

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var token = ReadString(document.RootElement, "token", "Token");
        var customerId = ReadGuid(document.RootElement, "customerId", "CustomerId");
        return (token, customerId);
    }

    private static async Task<string> ReadStringPropertyAsync(HttpResponseMessage response, params string[] names)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return ReadString(document.RootElement, names);
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }

        throw new Xunit.Sdk.XunitException($"No se encontró ninguna de estas propiedades: {string.Join(", ", names)}");
    }

    private static int ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value))
            {
                return value;
            }
        }

        throw new Xunit.Sdk.XunitException($"No se encontró un entero válido en: {string.Join(", ", names)}");
    }

    private static Guid ReadGuid(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
                Guid.TryParse(property.GetString(), out var value))
            {
                return value;
            }
        }

        throw new Xunit.Sdk.XunitException($"No se encontró un GUID válido en: {string.Join(", ", names)}");
    }
}
