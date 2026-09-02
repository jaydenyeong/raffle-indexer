using System.Numerics;
using RaffleIndexer.Data;
using RaffleIndexer.Indexing;
using RaffleIndexer.Chain;

namespace RaffleIndexer.UnitTests;

public class RaffleProjectorTests
{
    private const string Alice = "0xaaaa000000000000000000000000000000000001";
    private const string Bob = "0xbbbb000000000000000000000000000000000002";

    private static DateTimeOffset At(long block) =>
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + block * 12);
    
    private static RaffleEvent Enter(string player, long block, int logIndex = 0) =>
        new(RaffleEventKind.Enter, block, logIndex, $"0xtx{block}_{logIndex}", Address: player)
        {BlockTime = At(block)};

    private static RaffleEvent Requested(BigInteger requestId, long block, int logIndex = 0) =>
        new(RaffleEventKind.Requested, block, logIndex, $"0xtx{block}_{logIndex}", RequestId: requestId)
        {BlockTime = At(block)};

    private static RaffleEvent Won(string winner, long block, BigInteger prizeWei, int logIndex = 0) =>
        new(RaffleEventKind.WinnerPicked, block, logIndex, $"0xtx{block}_{logIndex}", Address: winner)
        {BlockTime = At(block), PrizeWei = prizeWei};

    [Fact]
    public void Opens_round_one_lazily_on_the_first_entry()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty, [Enter(Alice, 100)]);

        var round = Assert.Single(result.TouchedRounds);
        Assert.Equal(1, round.Id);
        Assert.Equal(RoundStatus.Open, round.Status);
        Assert.Equal(100, round.OpenedAtBlock);
        Assert.Equal(At(100), round.OpenedAtTime);
        Assert.Equal(1, round.EntryCount);
    }
    [Fact]
    public void An_empty_batch_produces_no_phantom_round()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty, []);

        Assert.Empty(result.TouchedRounds);
        Assert.Empty(result.NewEntries);
    }

    [Fact]
    public void Runs_a_full_round_from_entries_through_settlement()
    {
        var prize = BigInteger.Parse("30000000000000000");

        var result = RaffleProjector.Project(ProjectionState.Empty, [
            Enter(Alice, 100),
            Enter(Bob, 101),
            Enter(Alice, 102, logIndex: 1),
            Requested(new BigInteger(777), 110),
            Won(Bob, 115, prize)
        ]);

        var round = Assert.Single(result.TouchedRounds);
        Assert.Equal(1, round.Id);
        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.Equal(3, round.EntryCount);
        Assert.Equal(110, round.RequestedAtBlock);
        Assert.Equal(new BigInteger(777), round.RequestId);
        Assert.Equal(115, round.SettledAtBlock);
        Assert.Equal(At(115), round.SettledAtTime);
        Assert.Equal(Bob, round.WinnerAddress);
        Assert.Equal(prize, round.PrizeWei);

        Assert.Equal(3, result.NewEntries.Count);
        Assert.All(result.NewEntries, e => Assert.Equal(1, e.RoundId));
        Assert.Equal([Alice, Bob, Alice], result.NewEntries.Select(e => e.PlayerAddress));
    }

    [Fact]
    public void Opens_the_next_round_after_settlement()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty,
        [
            Enter(Alice, 100),
            Requested(new BigInteger(1), 110),
            Won(Alice, 115, BigInteger.One),
            Enter(Bob, 120)
        ]);

        Assert.Equal(2, result.TouchedRounds.Count);

        var first = result.TouchedRounds.Single(r => r.Id == 1);
        Assert.Equal(RoundStatus.Settled, first.Status);

        var second = result.TouchedRounds.Single(r => r.Id == 2);
        Assert.Equal(RoundStatus.Open, second.Status);
        Assert.Equal(120, second.OpenedAtBlock);
        Assert.Equal(1, second.EntryCount);
    }

    [Fact]
    public void Processes_events_in_block_and_log_index_order_regardless_of_input_order()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty,
        [
            Enter(Bob, 100, logIndex: 3),
            Enter(Alice, 100, logIndex: 1)
        ]);
        Assert.Equal([Alice, Bob], result.NewEntries.Select(e => e.PlayerAddress));
    }

    [Fact]
    public void A_round_survives_a_chunk_boundary()
    {
        var first = RaffleProjector.Project(ProjectionState.Empty,
        [Enter(Alice,100), Enter(Bob, 101)]);

        var carried = Assert.Single(first.TouchedRounds);
        Assert.Equal(RoundStatus.Open, carried.Status);

        var second = RaffleProjector.Project(
            new ProjectionState(LastRoundId: 1, ActiveRound: carried),
            [Requested(new BigInteger(5), 110), Won(Alice, 115, new BigInteger(20))]
        );

        var settled = Assert.Single(second.TouchedRounds);
        Assert.Equal(1, settled.Id);
        Assert.Equal(RoundStatus.Settled, settled.Status);

        Assert.Equal(2, settled.EntryCount);
        Assert.Equal(100, settled.OpenedAtBlock);
    }
    
    [Fact]
    public void A_round_whose_VRF_never_fulfills_stays_Calculating()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty,
        [Enter(Alice, 100), Requested(new BigInteger(9), 110)]);

        var round = Assert.Single(result.TouchedRounds);
        Assert.Equal(RoundStatus.Calculating, round.Status);
        Assert.Null(round.SettledAtBlock);
        Assert.Null(round.WinnerAddress);
        Assert.Null(round.PrizeWei);
    }

    [Fact]
    public void Replaying_the_same_batch_produces_identical_output()
    {
        RaffleEvent[] batch =
        [
            Enter(Alice, 100),
            Requested(new BigInteger(1), 110),
            Won(Alice, 115, new BigInteger(7))
        ];

        var first = RaffleProjector.Project(ProjectionState.Empty, batch);
        var second = RaffleProjector.Project(ProjectionState.Empty, batch);

        Assert.Equal(first.TouchedRounds, second.TouchedRounds);
        Assert.Equal(first.NewEntries, second.NewEntries);
    }
    [Fact]
    public void Throws_when_settlement_arrives_with_no_round_in_flight()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RaffleProjector.Project(ProjectionState.Empty, [Won(Alice, 115, BigInteger.One)]));

        Assert.Contains("no round is in flight", ex.Message);
    }
}