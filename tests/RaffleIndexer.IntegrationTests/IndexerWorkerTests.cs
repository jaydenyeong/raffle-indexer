using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaffleIndexer;
using RaffleIndexer.Chain;
using RaffleIndexer.Data;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class IndexerWorkerTests(PostgresFixture fixture)
{
    private const string Alice = "0xaaaa000000000000000000000000000000000001";
    private const long Start = 11514209;

    private static RaffleOptions Options() => new()
    {
        RpcUrl = "http://unused",
        StartBlock = Start,
        ChunkSize = 2000,
        ConfirmationBlocks = 5,
        PollIntervalSeconds = 1
    };

    private static IndexerWorker Worker(FakeChainClient chain) =>
        new(chain,
            serviceScopeFactory: null!,   // RunOnceAsync takes the store directly
            Microsoft.Extensions.Options.Options.Create(Options()),
            NullLogger<IndexerWorker>.Instance);

    private static RaffleEvent Enter(string player, long block, int logIndex = 0) =>
        new(RaffleEventKind.Enter, block, logIndex, $"0xtx{block}_{logIndex}", Address: player);

    [Fact]
    public async Task Indexes_one_chunk_and_advances_the_cursor()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 10 };
        chain.Events.Add(Enter(Alice, Start + 1));

        await using var db = fixture.CreateContext();
        var indexed = await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        Assert.True(indexed);
        Assert.Equal((Start, Start + 5), chain.GetEventsCalls.Single());

        await using var read = fixture.CreateContext();
        var cursor = await read.Cursor.SingleAsync();
        Assert.Equal(Start + 5, cursor.LastIndexedBlock);   // head - 5 confirmations
        Assert.Equal(Start + 10, cursor.ChainHeadBlock);
        Assert.Equal(1, await read.Rounds.CountAsync());
        Assert.Equal(1, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Does_nothing_when_already_caught_up_to_the_safe_head()
    {
        await fixture.ResetAsync();
        // Safe head is Start - 1, exactly where the seeded cursor sits.
        var chain = new FakeChainClient { Head = Start + 4 };

        await using var db = fixture.CreateContext();
        var indexed = await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        Assert.False(indexed);
        Assert.Empty(chain.GetEventsCalls);
    }

    [Fact]
    public async Task Backfills_across_several_chunks_carrying_a_round_over_the_boundary()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 4005 };
        chain.Events.Add(Enter(Alice, Start + 10));          // chunk 1
        chain.Events.Add(Enter(Alice, Start + 2500, 1));     // chunk 2, same round

        await using (var db = fixture.CreateContext())
        {
            var worker = Worker(chain);
            var store = new IndexerStore(db);
            Assert.True(await worker.RunOnceAsync(store, CancellationToken.None));
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.True(await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None));
        }

        Assert.Equal([(Start, Start + 1999), (Start + 2000, Start + 3999)], chain.GetEventsCalls);

        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(1, round.Id);
        Assert.Equal(2, round.EntryCount);      // the round spanned the chunk boundary
        Assert.Equal(2, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Derives_the_prize_from_the_balance_one_block_before_settlement()
    {
        await fixture.ResetAsync();
        var prize = BigInteger.Parse("30000000000000000");
        var chain = new FakeChainClient { Head = Start + 100 };
        chain.Events.Add(Enter(Alice, Start + 1));
        chain.Events.Add(new RaffleEvent(RaffleEventKind.Requested, Start + 2, 0, "0xr", RequestId: new BigInteger(7)));
        chain.Events.Add(new RaffleEvent(RaffleEventKind.WinnerPicked, Start + 3, 0, "0xw", Address: Alice));
        // fulfillRandomWords forwards the whole balance, so the prize is the
        // balance at the end of the previous block.
        chain.BalancesByBlock[Start + 2] = prize;

        await using var db = fixture.CreateContext();
        await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.Equal(prize, round.PrizeWei);
        Assert.Equal(Alice, round.WinnerAddress);
    }

    [Fact]
    public async Task Fetches_each_blocks_timestamp_only_once_per_batch()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 100 };
        // Three entries in one block: one eth_getBlockByNumber, not three.
        chain.Events.Add(Enter(Alice, Start + 1, 0));
        chain.Events.Add(Enter(Alice, Start + 1, 1));
        chain.Events.Add(Enter(Alice, Start + 1, 2));

        await using var db = fixture.CreateContext();
        await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        Assert.Equal([Start + 1], chain.TimestampCalls);
    }

    [Fact]
    public async Task Re_running_the_same_chunk_writes_no_duplicates()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 10 };
        chain.Events.Add(Enter(Alice, Start + 1));

        await using (var db = fixture.CreateContext())
            await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        // Simulate a crash after commit but before the cursor was read back.
        await using (var db = fixture.CreateContext())
        {
            var cursor = await db.Cursor.SingleAsync();
            cursor.LastIndexedBlock = Start - 1;
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.Entries.CountAsync());
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(1, round.EntryCount);
    }
}
