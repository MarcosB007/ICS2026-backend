using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dsw2025Tpi.Application.Dtos;
using Dsw2025Tpi.Data;
using Dsw2025Tpi.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Utilidades compartidas por las pruebas de integración: autenticación (JWT reales),
/// siembra directa en SQLite y lectura directa de la base mediante el DbContext.
/// </summary>
internal static class TestHelpers
{
    // ---------- Autenticación ----------

    public static async Task<HttpClient> CreateAdminClientAsync(CustomWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginModel("admin", "Admin123!"));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "el administrador de prueba debe estar cargado desde admins.json");

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        SetBearer(client, ReadString(document.RootElement, "token", "Token"));
        return client;
    }

    /// <summary>Registra un usuario con rol USER (no admin) y devuelve un cliente HTTP autenticado.</summary>
    public static async Task<(HttpClient Client, Guid CustomerId)> CreateCustomerClientAsync(
        CustomWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var uniqueId = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterModel(
            Username: $"user{uniqueId}",
            Password: "Test123!",
            Email: $"user{uniqueId}@integration.local"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "el usuario de prueba debe poder registrarse");

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        SetBearer(client, ReadString(document.RootElement, "token", "Token"));
        var customerId = TryReadGuid(document.RootElement, "customerId", "CustomerId") ?? Guid.Empty;
        return (client, customerId);
    }

    private static void SetBearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ---------- Base de datos (SQLite) ----------

    /// <summary>Inserta un producto directamente en SQLite usando el DbContext real.</summary>
    public static async Task<Product> SeedProductAsync(
        CustomWebApplicationFactory factory,
        string? sku = null,
        string? name = null,
        int stock = 10,
        bool isActive = true,
        decimal price = 100m)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var product = NewEntity<Product>();
        SetProperty(product, "Id", Guid.NewGuid());
        SetProperty(product, "Sku", sku ?? $"SKU-SEED-{uniqueId}");
        SetProperty(product, "InternalCode", $"INT-SEED-{uniqueId}");
        SetProperty(product, "Name", name ?? $"Producto sembrado {uniqueId[..8]}");
        SetProperty(product, "Description", "Producto sembrado por una prueba de integración");
        SetProperty(product, "CurrentUnitPrice", price);
        SetProperty(product, "StockQuantity", stock);
        SetProperty(product, "IsActive", isActive);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>Lee un producto directamente de SQLite (sin pasar por la API).</summary>
    public static async Task<Product?> GetProductFromDbAsync(CustomWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        return await db.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public static async Task<Product?> GetProductFromDbBySkuAsync(CustomWebApplicationFactory factory, string sku)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        return await db.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Sku == sku);
    }

    // ---------- Reflection (permite sembrar entidades aunque tengan setters/constructores privados) ----------

    private static T NewEntity<T>() where T : class
    {
        try
        {
            return (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
        }
        catch (MissingMethodException)
        {
            return (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        }
    }

    private static void SetProperty(object target, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (property == null) continue;

            var setter = property.GetSetMethod(nonPublic: true);
            if (setter != null)
            {
                setter.Invoke(target, new[] { value });
                return;
            }

            var backingField = type.GetField($"<{name}>k__BackingField", flags);
            if (backingField != null)
            {
                backingField.SetValue(target, value);
                return;
            }
        }

        throw new InvalidOperationException(
            $"La entidad {target.GetType().Name} no tiene la propiedad '{name}'. Ajustá TestHelpers.SeedProductAsync.");
    }

    // ---------- JSON ----------

    public static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException($"No se encontró ninguna de estas propiedades: {string.Join(", ", names)}");
    }

    public static Guid? TryReadGuid(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                && Guid.TryParse(property.GetString(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    public static ProductModel.Request NewProductRequest(string prefix = "TEST", int stock = 10)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        return new ProductModel.Request(
            Sku: $"SKU-{prefix}-{uniqueId}",
            InternalCode: $"INT-{prefix}-{uniqueId}",
            Name: $"Producto {prefix} {uniqueId[..8]}",
            Description: "Producto creado durante una prueba de integración",
            CurrentUnitPrice: 100m,
            StockQuantity: stock);
    }
}
