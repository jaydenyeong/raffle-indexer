using RaffleIndexer.Chain;
using System.Numerics;

namespace RaffleIndexer.IntergrationTests;

public class FakeChainClient : IChainClient
{
    public long Head {get; set;}
    public List<RaffleEvent> Events {get;} = [];

    public Dictionary<long, BigInteger> BalancesByBlock {get;} = [];

    public BigInteger EntranceFee {get; set;} = BigInteger.Parse("10000000000000000");

    public List<(long From, long To)> GetEventsCalls {get;} = [];
    public List<long> TimestampCalls {get;} = [];
    public Task<long> GetLatestBlockNumberAsync(CancellationToken ct) => Task.FromResult(Head);

    public Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(long fromBlock, long toBlock, CancellationToken ct)
    {
        GetEventsCalls.Add((fromBlock, toBlock));
        IReadOnlyList<RaffleEvent> inRange = Events
            .Where(e => e.BlockNumber >= fromBlock && e.BlockNumber <= toBlock)
            .OrderBy(e => e.BlockNumber).ThenBy(e => e.LogIndex)
            .ToList();
        return Task.FromResult(inRange);
    }

    public Task<DateTimeOffset> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
    {
        TimestampCalls.Add(blockNumber);
        return Task.FromResult(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + blockNumber * 12));
    }

    public Task<BigInteger> GetBalanceAtBlockAsync(long blockNumber, CancellationToken ct) =>
        Task.FromResult(BalancesByBlock.TryGetValue(blockNumber, out var b)? b: BigInteger.Zero);

    public Task<BigInteger> GetEntranceFeeAsync(CancellationToken ct) => Task.FromResult(EntranceFee);
}