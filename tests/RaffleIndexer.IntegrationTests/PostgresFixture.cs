using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using Testcontainers.PostgreSql;

namespace RaffleIndexer.IntegrationTests;

public class PostgresFixture : IAsyncLifetime
{
    // Testcontainers 4.14+ deprecated the parameterless constructor; the image
    // is passed to the constructor instead of via .WithImage().
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .Build();
    
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync() => await _container.DisposeAsync();

    public IndexerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IndexerDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IndexerDbContext(options);
    }

    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE entries, rounds, indexer_metadata, indexer_cursor RESTART IDENTITY CASCADE;"
        );
        db.Cursor.Add(new IndexerCursor
        {
            Id = IndexerDbContext.CursorRowId,
            LastIndexedBlock = 11514208,
            ChainHeadBlock = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;