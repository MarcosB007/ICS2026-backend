using Dsw2025Tpi.Data;
using Dsw2025Tpi.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dsw2025Tpi.IntegrationTests;

[Collection("IntegrationTests")]
public class InfraestructuraTests
{
    private readonly CustomWebApplicationFactory _factory;

    public InfraestructuraTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Dummy_VerificaQueElProyectoSeDescubreYEjecuta()
    {
        true.Should().BeTrue();
    }

    [Fact]
    public void Factory_UsaSqliteEnMemoriaConElEsquemaCreado()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Dsw2025TpiContext>();
        var auth = scope.ServiceProvider.GetRequiredService<AuthenticateContext>();

        // Act
        var consultarProductos = () => db.Set<Product>().Count();

        // Assert
        db.Database.IsSqlite().Should().BeTrue();
        auth.Database.IsSqlite().Should().BeTrue();
        consultarProductos.Should().NotThrow();
    }
}
