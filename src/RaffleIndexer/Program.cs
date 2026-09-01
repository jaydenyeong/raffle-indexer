using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<IndexerDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
    .UseSnakeCaseNamingConvention());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new {status = "starting"}));

app.Run();
