using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using Microsoft.Extensions.Options;
using RaffleIndexer;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RaffleOptions>(
    builder.Configuration.GetSection(RaffleOptions.SectionName));

builder.Services.AddDbContext<IndexerDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<RaffleIndexer.Chain.IChainClient, RaffleIndexer.Chain.ChainClient>();

builder.Services.AddScoped<IIndexerStore, IndexerStore>();

if (builder.Configuration.GetValue($"{RaffleOptions.SectionName}:EnableIndexer", true))
{
    builder.Services.AddHostedService<RaffleIndexer.Indexing.IndexerWorker>();
}

builder.Services.AddOpenApi();

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

app.MapOpenApi();
app.MapScalarApiReference();

RaffleIndexer.Api.RoundEndpoints.Map(app);
RaffleIndexer.Api.MiscEndpoints.Map(app);

app.MapGet("/", () => Results.Redirect("/scalar/v1"));

app.Run();

public partial class Program;
