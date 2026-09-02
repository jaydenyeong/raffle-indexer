using System.Numerics;

namespace RaffleIndexer.Chain;

/// <summary> the only surface the worker needs to know about the chain. </summary>

public interface IChainClient
{
    Task<long> GetLatestBlockNumberAsync(CancellationToken ct);

    /// <summary> Get logs for the given block range, inclusive. </summary>
    Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(long fromBlock, long toBlock, CancellationToken ct);

    Task<DateTimeOffset> GetBlockTimestampAsync(long blockNumber, CancellationToken ct);

    /// <summary> Contract ETH balance as of the end of the given block </summary>
    Task<BigInteger> GetBalanceAtBlockAsync(long blockNumber, CancellationToken ct);

    Task<BigInteger> GetEntranceFeeAsync(CancellationToken ct);
}