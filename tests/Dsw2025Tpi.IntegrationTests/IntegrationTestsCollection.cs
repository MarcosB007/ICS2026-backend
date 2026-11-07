namespace Dsw2025Tpi.IntegrationTests;

/// <summary>
/// Todas las clases marcadas con [Collection("IntegrationTests")] comparten
/// una única instancia de CustomWebApplicationFactory y se ejecutan en serie,
/// evitando colisiones de concurrencia sobre la base SQLite.
/// </summary>
[CollectionDefinition("IntegrationTests")]
public class IntegrationTestsCollection : ICollectionFixture<CustomWebApplicationFactory>
{
}
