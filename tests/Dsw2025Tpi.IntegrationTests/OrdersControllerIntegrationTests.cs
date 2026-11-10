using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dsw2025Tpi.Data;
using Dsw2025Tpi.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Creación de órdenes (POST /api/orders): JWT obligatorio, transacción completa
/// (orden + descuento de stock) y manejo de stock insuficiente.
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
        // Arrange: payload de orden válido en forma, pero sin header Authorization.
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
    public async Task CreateOrder_WhenAuthenticatedWithValidData_ReturnsCreatedAndPersistsOrderAndStockInSqlite()
    {
        // Arrange: cliente autenticado (JWT real) + producto sembrado en SQLite con stock 10.
        var (customerClient, customerId) = await TestHelpers.CreateCustomerClientAsync(_factory);
        using var customerClientScope = customerClient;
        var product = await TestHelpers.SeedProductAsync(_factory, stock: 10);

        var request = new
        {
            CustomerId = customerId,
            ShippingAddress = "Av. Siempre Viva 742",
            BillingAddress = "Av. Siempre Viva 742",
            Notes = "Prueba de integración de órdenes",
            Items = new[] { new { ProductId = product.Id, Quantity = 3 } }
        };

        // Act
        var response = await customerClient.PostAsJsonAsync("/api/orders", request);

        // Assert: 201 Created + header Location
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var orderId = TestHelpers.TryReadGuid(body.RootElement, "orderId", "OrderId", "id", "Id");
        orderId.Should().NotBeNull("la respuesta debe incluir el identificador de la orden creada");

        // Assert: la orden existe en la tabla Orders de SQLite.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
            var order = await db.Set<Order>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            order.Should().NotBeNull("la orden debe haberse insertado en la base de datos");
        }

        // Assert: el stock en la tabla Products bajó de 10 a 7.
        var updated = await TestHelpers.GetProductFromDbAsync(_factory, product.Id);
        updated.Should().NotBeNull();
        updated!.StockQuantity.Should().Be(7);
    }

    [Fact]
    public async Task CreateOrder_WhenStockIsInsufficient_ReturnsBadRequestWithErrorMessage()
    {
        // Arrange: el producto tiene 2 unidades y se piden 5.
        var (customerClient, customerId) = await TestHelpers.CreateCustomerClientAsync(_factory);
        using var customerClientScope = customerClient;
        var product = await TestHelpers.SeedProductAsync(_factory, stock: 2);

        var request = new
        {
            CustomerId = customerId,
            ShippingAddress = "Calle de prueba 456",
            BillingAddress = "Calle de prueba 456",
            Notes = "Prueba de stock insuficiente",
            Items = new[] { new { ProductId = product.Id, Quantity = 5 } }
        };

        // Act
        var response = await customerClient.PostAsJsonAsync("/api/orders", request);

        // Assert: 400 (no 500) con un JSON que explica la falta de stock.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Contain("json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrWhiteSpace();
        using var json = JsonDocument.Parse(content); // debe ser JSON válido
        content.Should().MatchRegex("(?i)stock", "el mensaje debe indicar claramente la falta de stock");

        // El stock no debe haberse modificado.
        var unchanged = await TestHelpers.GetProductFromDbAsync(_factory, product.Id);
        unchanged!.StockQuantity.Should().Be(2);
    }
}
