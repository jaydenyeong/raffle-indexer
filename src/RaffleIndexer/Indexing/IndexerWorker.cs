using System.Numerics;
using Microsoft.Extensions.Options;
using RaffleIndexer.Chain;
using RaffleIndexer.Data;

namespace RaffleIndexer.Indexing;

public class IndexerWorker(
    IChainClient chain,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<RaffleOptions> options,
    ILogger<IndexerWorker> logger) : BackgroundService
{
    private readonly RaffleOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecordEntranceFeeAsync(stoppingToken);

        var idleDelay = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IIndexerStore>();

                var indexed = await RunOnceAsync(store, stoppingToken);

                if (!indexed) await Task.Delay(idleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Indexing pass failed: retrying after {Delay}", idleDelay);
                await Task.Delay(idleDelay, stoppingToken);
            }
        }
    }

    public async Task<bool> RunOnceAsync(IIndexerStore store, CancellationToken ct)
    {
        var chainHead = await chain.GetLatestBlockNumberAsync(ct);
        var lastIndexed = await store.GetLastIndexedBlockAsync(ct);

        var range = BlockRange.Next(lastIndexed, chainHead, _options.ChunkSize, _options.ConfirmationBlocks);
        if (range is null) return false;

        var (from, to) = range.Value;

        var events = await chain.GetEventsAsync(from, to, ct);
        var enriched = await EnrichAsync(events, ct);

        var state = await store.LoadProjectionStateAsync(ct);
        var result = RaffleProjector.Project(state, enriched);

        await store.CommitBatchAsync(result, to, chainHead, ct);

        if (result.TouchedRounds.Count > 0 || result.NewEntries.Count > 0)
        {
            logger.LogInformation(
                "Indexed blocks {From}-{To}: {Rounds} round(s), {Entries} entry/entries",
                from, to, result.TouchedRounds.Count, result.NewEntries.Count
            );
        }
        return true;
    }

    public async Task<IReadOnlyList<RaffleEvent>> EnrichAsync(
        IReadOnlyList<RaffleEvent> events, CancellationToken ct)
    {
        var timestamps = new Dictionary<long, DateTimeOffset>();
        var enriched = new List<RaffleEvent>(events.Count);

        foreach (var e in events)
        {
            if (!timestamps.TryGetValue(e.BlockNumber, out var blockTime))
            {
                blockTime = await chain.GetBlockTimestampAsync(e.BlockNumber, ct);
                timestamps[e.BlockNumber] = blockTime;
            }

            BigInteger? prize = null;
            if (e.Kind == RaffleEventKind.WinnerPicked)
            {
                prize = await chain.GetBalanceAtBlockAsync(e.BlockNumber - 1, ct);
            }
            enriched.Add(e with {BlockTime = blockTime, PrizeWei = prize});
        }
        return enriched;
    }
    private async Task RecordEntranceFeeAsync(CancellationToken ct)
    {
        try
        {
            var fee = await chain.GetEntranceFeeAsync(ct);

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIndexerStore>();
            await store.SetEntranceFeeAsync(fee, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read getEntranceFee(); continuing without it");
        }
    }
}