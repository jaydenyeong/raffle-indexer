using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RaffleIndexer.Data;

namespace RaffleIndexer.Api;

public static class MiscEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/players/{address}", async (IndexerDbContext db, string address) =>
        {
            // Storage is lowercase, so normalizing the input is all the
            // case-insensitivity this needs — no LOWER() on the column, no lost index.
            var lower = address.ToLowerInvariant();

            var entries = await db.Entries.AsNoTracking()
                .Where(e => e.PlayerAddress == lower)
                .OrderByDescending(e => e.BlockNumber).ThenByDescending(e => e.LogIndex)
                .ToListAsync();

            var wins = await db.Rounds.AsNoTracking()
                .Where(r => r.WinnerAddress == lower)
                .Select(r => r.PrizeWei)
                .ToListAsync();

            var totalWon = wins.Aggregate(BigInteger.Zero, (sum, prize) => sum + (prize ?? BigInteger.Zero));

            return Results.Ok(new PlayerView(
                AddressFormatting.ToChecksum(lower)!,
                RoundsPlayed: entries.Select(e => e.RoundId).Distinct().Count(),
                EntryCount: entries.Count,
                Wins: wins.Count,
                TotalWonWei: totalWon.ToString(),
                Entries: entries.Select(RoundEndpoints.ToEntryView).ToList()));
        })
        .WithSummary("A player's entries, rounds played, wins, and total winnings.");

        app.MapGet("/stats", async (IndexerDbContext db) =>
        {
            var prizes = await db.Rounds.AsNoTracking()
                .Where(r => r.Status == RoundStatus.Settled && r.PrizeWei != null)
                .Select(r => r.PrizeWei!.Value)
                .ToListAsync();

            var entranceFee = await db.Metadata.AsNoTracking()
                .Where(m => m.Key == IndexerDbContext.EntranceFeeKey)
                .Select(m => m.Value)
                .FirstOrDefaultAsync();

            return Results.Ok(new StatsView(
                RoundsSettled: await db.Rounds.CountAsync(r => r.Status == RoundStatus.Settled),
                RoundsTotal: await db.Rounds.CountAsync(),
                TotalPaidWei: prizes.Aggregate(BigInteger.Zero, (sum, p) => sum + p).ToString(),
                UniquePlayers: await db.Entries.Select(e => e.PlayerAddress).Distinct().CountAsync(),
                BiggestPrizeWei: prizes.Count == 0 ? null : prizes.Max().ToString(),
                EntranceFeeWei: entranceFee));
        })
        .WithSummary("Rounds settled, total paid out, unique players, biggest prize.");

        app.MapGet("/health", async (IndexerDbContext db, IOptions<RaffleOptions> options) =>
        {
            var cursor = await db.Cursor.AsNoTracking()
                .SingleAsync(c => c.Id == IndexerDbContext.CursorRowId);

            var behind = Math.Max(0, cursor.ChainHeadBlock - cursor.LastIndexedBlock);

            // The head comes from the cursor row, written by the worker: this
            // endpoint reads Postgres like every other one.
            return Results.Ok(new HealthView(
                cursor.LastIndexedBlock,
                cursor.ChainHeadBlock,
                behind,
                CaughtUp: cursor.ChainHeadBlock > 0 && behind <= options.Value.ConfirmationBlocks,
                cursor.UpdatedAt));
        })
        .WithSummary("Indexing progress: last indexed block, chain head, blocks behind.");
    }
}
