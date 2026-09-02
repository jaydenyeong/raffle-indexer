using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using Microsoft.Extensions.Options;
using RaffleIndexer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RaffleOptions>(
    builder.Configuration.GetSection(RaffleOptions.SectionName));

builder.Services.AddDbContext<IndexerDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<RaffleIndexer.Chain.IChainClient, RaffleIndexer.Chain.ChainClient>();

var app = builder.Build();

// single-service deployment

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IndexerDbContext>();
    var options = scope.ServiceProvider.GetRequiredService<IOptions<RaffleOptions>>().Value;

    await db.Database.MigrateAsync();

    if (!await db.Cursor.AnyAsync())
    {
        db.Cursor.Add(new IndexerCursor
        {
            Id = IndexerDbContext.CursorRowId,
            LastIndexedBlock = options.StartBlock - 1,
            ChainHeadBlock = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", async (IndexerDbContext db) =>
{
    var cursor = await db.Cursor.AsNoTracking().SingleAsync(c => c.Id == IndexerDbContext.CursorRowId);

    var behind = Math.Max(0, cursor.ChainHeadBlock - cursor.LastIndexedBlock);

    return Results.Ok(new
    {
        lastIndexedBlock = cursor.LastIndexedBlock,
        chainHeadBlock = cursor.ChainHeadBlock,
        blocksBehind = behind,
        caughtUp = cursor.ChainHeadBlock > 0 && behind <= 5,
        updatedAt = cursor.UpdatedAt
    });
});

app.Run();

public partial class Program;
