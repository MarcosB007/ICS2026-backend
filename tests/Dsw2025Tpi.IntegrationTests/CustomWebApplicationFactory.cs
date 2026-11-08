using Dsw2025Tpi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Levanta la API en un servidor de prueba en memoria, con entorno "Testing"
/// y SQLite en memoria compartida en lugar de SQL Server.
/// Cada DbContext usa su propia base en memoria; las conexiones quedan abiertas
/// para que las bases vivan durante toda la sesión de pruebas.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Dsw2025Tpi.Api.Program>
{
    private readonly SqliteConnection _mainConnection =
        new("DataSource=Dsw2025TpiTests;Mode=Memory;Cache=Shared");

    private readonly SqliteConnection _authConnection =
        new("DataSource=Dsw2025TpiAuthTests;Mode=Memory;Cache=Shared");

    public CustomWebApplicationFactory()
    {
        _mainConnection.Open();
        _authConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Quitar la configuración de SQL Server registrada en Program.cs
            // (en EF Core 9 hay que quitar también IDbContextOptionsConfiguration<T>)
            services.RemoveAll<DbContextOptions<Dsw2025TpiContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Dsw2025TpiContext>>();
            services.RemoveAll<DbContextOptions<AuthenticateContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AuthenticateContext>>();

            // Registrar ambos contextos sobre SQLite en memoria
            services.AddDbContext<Dsw2025TpiContext>(options => options.UseSqlite(_mainConnection));
            services.AddDbContext<AuthenticateContext>(options => options.UseSqlite(_authConnection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _mainConnection.Dispose();
            _authConnection.Dispose();
        }
    }
}
