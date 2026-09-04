using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RaffleIndexer.IntegrationTests;

/// <summary>
/// Boots the real app against the Testcontainers database, with the indexer
/// switched off - these tests assert the API reads Postgres, never the chain
/// </summary>

public class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Raffle:EnableIndexer"] = "false",
                ["Raffle:RpcUrl"] = "http://unused"
            }));
        return base.CreateHost(builder);
    }
}