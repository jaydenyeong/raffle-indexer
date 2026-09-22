using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using Microsoft.Extensions.Options;
using RaffleIndexer;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RaffleOptions>(
    builder.Configuration.GetSection(RaffleOptions.SectionName));

builder.Services.AddDbContext<IndexerDbContext>(o =>
    // Normalize handles the postgres:// URL that managed hosts supply; a local
    // key-value connection string passes through unchanged.
    o.UseNpgsql(DatabaseUrl.Normalize(builder.Configuration.GetConnectionString("Default")))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<RaffleIndexer.Chain.IChainClient, RaffleIndexer.Chain.ChainClient>();

builder.Services.AddScoped<IIndexerStore, IndexerStore>();

if (builder.Configuration.GetValue($"{RaffleOptions.SectionName}:EnableIndexer", true))
{
    builder.Services.AddHostedService<RaffleIndexer.Indexing.IndexerWorker>();
}

builder.Services.AddOpenApi();

// Render, and every other managed host, terminates TLS at a proxy and forwards
// to the container over plain HTTP. Without this the app believes the request
// arrived on http, advertises "http://..." in the OpenAPI servers array, and the
// browser blocks the interactive page's own calls as mixed content.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // The proxy's address is not known ahead of time, and on these platforms the
    // container is only reachable through it, so the defaults are cleared rather
    // than pinned to a network.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Must run before anything that reads the request scheme.
app.UseForwardedHeaders();

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
