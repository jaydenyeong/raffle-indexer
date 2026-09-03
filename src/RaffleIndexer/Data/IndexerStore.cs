using System.Numerics;
using RaffleIndexer.Indexing;
using Microsoft.EntityFrameworkCore;

namespace RaffleIndexer.Data;

public class IndexerStore(IndexerDbContext db) : IIndexerStore
{
    public async Task<long> GetLastIndexedBlockAsync(CancellationToken ct)
    {
        var cursor = await db.Cursor.AsNoTracking()
            .SingleAsync(c => c.Id == IndexerDbContext.CursorRowId, ct);
        return cursor.LastIndexedBlock;
    }
    public async Task<ProjectionState> LoadProjectionStateAsync(CancellationToken ct)
    {
        var lastRoundId = await db.Rounds.AsNoTracking().MaxAsync(r => (int?)r.Id, ct) ?? 0;

        var active = await db.Rounds.AsNoTracking()
            .Where(r => r.Status != RoundStatus.Settled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        return new ProjectionState(lastRoundId, active is null ? null : ToSnapshot(active));
    }

    public async Task CommitBatchAsync(
        ProjectionResult result, long lastIndexedBlock, long chainHead, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await UpsertRoundsAsync(result.TouchedRounds, ct);
        await InsertNewEntriesAsync(result.NewEntries, ct);

        var cursor = await db.Cursor.SingleAsync(c => c.Id == IndexerDbContext.CursorRowId, ct);
        cursor.LastIndexedBlock = lastIndexedBlock;
        cursor.ChainHeadBlock = chainHead;
        cursor.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        await RecountEntriesAsync(result.TouchedRounds, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetEntranceFeeAsync(BigInteger entranceFeeWei, CancellationToken ct)
    {
        var row = await db.Metadata.FindAsync([IndexerDbContext.EntranceFeeKey], ct);
        if (row is null)
        {
            db.Metadata.Add(new IndexerMetadata
            {
                Key = IndexerDbContext.EntranceFeeKey,
                Value = entranceFeeWei.ToString()
            });
        }
        else
        {
            row.Value = entranceFeeWei.ToString();
        }
        await db.SaveChangesAsync(ct);
    }
    private async Task UpsertRoundsAsync(IReadOnlyList<RoundSnapshot> rounds, CancellationToken ct)
    {
        if (rounds.Count == 0) return;

        var ids = rounds.Select(r => r.Id).ToList();
        var existing = await db.Rounds.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        foreach (var snapshot in rounds)
        {
            if (!existing.TryGetValue(snapshot.Id, out var row))
            {
                row = new Round { Id = snapshot.Id };
                db.Rounds.Add(row);
            }

            row.Status = snapshot.Status;
            row.OpenedAtBlock = snapshot.OpenedAtBlock;
            row.OpenedAtTime = snapshot.OpenedAtTime;
            row.RequestedAtBlock = snapshot.RequestedAtBlock;
            row.RequestId = snapshot.RequestId;
            row.SettledAtBlock = snapshot.SettledAtBlock;
            row.SettledAtTime = snapshot.SettledAtTime;
            row.WinnerAddress = snapshot.WinnerAddress;
            row.PrizeWei = snapshot.PrizeWei;
            row.EntryCount = snapshot.EntryCount;
        }
    }

    private async Task InsertNewEntriesAsync(IReadOnlyList<EntrySnapshot> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        // (tx_hash, log_index) is the idempotency key. Filtering here turns a
        // replayed chunk into a no-op instead of a unique-violation.
        var hashes = entries.Select(e => e.TxHash).Distinct().ToList();
        var known = await db.Entries
            .Where(e => hashes.Contains(e.TxHash))
            .Select(e => new { e.TxHash, e.LogIndex })
            .ToListAsync(ct);

        var seen = known.Select(k => (k.TxHash, k.LogIndex)).ToHashSet();

        foreach (var entry in entries)
        {
            // Add returns false if this key is already present, in the database
            // or earlier in this same batch.
            if (!seen.Add((entry.TxHash, entry.LogIndex))) continue;

            db.Entries.Add(new Entry
            {
                RoundId = entry.RoundId,
                PlayerAddress = entry.PlayerAddress,
                BlockNumber = entry.BlockNumber,
                BlockTime = entry.BlockTime,
                TxHash = entry.TxHash,
                LogIndex = entry.LogIndex
            });
        }
    }

    /// <summary>
    /// Makes entry_count a projection of the entries table rather than a
    /// running total.
    /// <para>
    /// The projector increments the count per event, which is correct on a
    /// first pass. But if a chunk is ever replayed, the deduped entry inserts
    /// become no-ops while the incremented count does not — the denormalized
    /// column drifts above the real row count. Recomputing here, inside the
    /// same transaction, makes the column self-healing and the replay a true
    /// no-op.
    /// </para>
    /// </summary>
    private async Task RecountEntriesAsync(IReadOnlyList<RoundSnapshot> rounds, CancellationToken ct)
    {
        if (rounds.Count == 0) return;

        var ids = rounds.Select(r => r.Id).ToList();

        var counts = await db.Entries
            .Where(e => ids.Contains(e.RoundId))
            .GroupBy(e => e.RoundId)
            .Select(g => new { RoundId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoundId, x => x.Count, ct);

        var rows = await db.Rounds.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.EntryCount = counts.GetValueOrDefault(row.Id, 0);
        }

        await db.SaveChangesAsync(ct);
    }

    private static RoundSnapshot ToSnapshot(Round r) => new(
        r.Id, r.Status, r.OpenedAtBlock, r.OpenedAtTime, r.RequestedAtBlock, r.RequestId,
        r.SettledAtBlock, r.SettledAtTime, r.WinnerAddress, r.PrizeWei, r.EntryCount);
}