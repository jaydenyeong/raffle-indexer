using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;

namespace RaffleIndexer.Api;

public static class RoundEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rounds", async (IndexerDbContext db, string? status, int page = 1, int pageSize = 20) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.Rounds.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<RoundStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new {error = $"Unknown status '{status}'. Use Open, Calculating, or Settled."});
                
                query = query.Where(r => r.Status == parsed);
            }

            var total = await query.CountAsync();

            var rounds = await query
                .OrderByDescending(r => r.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            
            return Results.Ok(new PagedResult<RoundSummary>(page, pageSize, total, rounds.Select(ToSummary).ToList()));
        })
        .WithSummary("Paged round summarizes, newest first. Optional status filter.");

        app.MapGet("/rounds/current", async (IndexerDbContext db) =>
        {
            var round = await db.Rounds.AsNoTracking()
                .Include(r => r.Entries)
                .Where(r => r.Status != RoundStatus.Settled)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
            return round is null ? Results.NotFound() : Results.Ok(ToDetail(round));
        })
        .WithSummary("The round currently Open or Calculating, if any.");
    }

    internal static RoundSummary ToSummary(Round r) => new(
        r.Id, r.Status.ToString(), r.OpenedAtBlock, r.OpenedAtTime,
        r.SettledAtBlock, r.SettledAtTime,
        AddressFormatting.ToChecksum(r.WinnerAddress),
        r.PrizeWei?.ToString(),
        r.EntryCount
    );

    internal static RoundDetail ToDetail(Round r) => new(
        r.Id, r.Status.ToString(), r.OpenedAtBlock, r.OpenedAtTime,
        r.RequestedAtBlock, r.RequestId?.ToString(),
        r.SettledAtBlock, r.SettledAtTime,
        AddressFormatting.ToChecksum(r.WinnerAddress),
        r.PrizeWei?.ToString(),
        r.EntryCount,
        r.Entries.OrderBy(e => e.BlockNumber).ThenBy(e => e.LogIndex).Select(ToEntryView).ToList()
    );

    internal static EntryView ToEntryView(Entry e) => new(
        AddressFormatting.ToChecksum(e.PlayerAddress)!, e.BlockNumber, e.BlockTime, e.TxHash, e.LogIndex
    );
}