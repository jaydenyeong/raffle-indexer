using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using RaffleIndexer.Indexing;
using System.Numerics;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class IndexerStoreTests(PostgresFixture fixture)
{
    private const string Alice = "0xaaaa000000000000000000000000000000000001";

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static RoundSnapshot OpenRound(int id, int entryCount) => new(
        id, RoundStatus.Open, 100, T0, null, null, null, null, null, null, entryCount
    );

    private static ProjectionResult Batch(RoundSnapshot round, params EntrySnapshot[] entries) =>
        new([round], entries);
    
    [Fact]
    public async Task Commits_rounds_entries_and_the_cursor_together()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();
        var store = new IndexerStore(db);

        await store.CommitBatchAsync(
            Batch(OpenRound(1, 1), new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0)),
            lastIndexedBlock: 11516209,
            chainHead: 11516214,
            CancellationToken.None
        );

        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.Rounds.CountAsync());
        Assert.Equal(1, await read.Entries.CountAsync());
        var cursor = await read.Cursor.SingleAsync();
        Assert.Equal(11516209, cursor.LastIndexedBlock);
        Assert.Equal(11516214, cursor.ChainHeadBlock);
    }

    [Fact]
    public async Task Replaying_the_same_batch_writes_no_duplicates()
    {
        await fixture.ResetAsync();
        var batch = Batch(OpenRound(1, 1), new EntrySnapshot(1, Alice, 100, T0, "0xaa",0));

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(batch, 11516209, 11516214, CancellationToken.None);
        
        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(batch, 11516209, 11516214, CancellationToken.None);
        
        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.Rounds.CountAsync());
        Assert.Equal(1, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Entry_count_matches_the_stored_entries_even_after_a_replay()
    {
        await fixture.ResetAsync();
        var entry = new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0);

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 1), entry), 11516209, 11516214, CancellationToken.None
            );

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 2), entry), 11516209, 11516214, CancellationToken.None
            );
        
        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(1, round.EntryCount);
        Assert.Equal(1, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Updates_an_existing_round_in_place_rather_than_inserting_a_second_one()
    {
        await fixture.ResetAsync();

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 1), new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0)),
                11516209, 11516214, CancellationToken.None
            );

        var settled = new RoundSnapshot(1, RoundStatus.Settled, 100, T0, 110, new BigInteger(42),
            115, T0.AddMinutes(5), Alice, BigInteger.Parse("30000000000000000"), 1);

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                new ProjectionResult([settled], []), 11518209, 11518214, CancellationToken.None
            );
        
        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.Equal(Alice, round.WinnerAddress);
        Assert.Equal(BigInteger.Parse("30000000000000000"), round.PrizeWei);
        Assert.Equal(new BigInteger(42), round.RequestId);
    }
    [Fact]
    public async Task The_cursor_does_not_advance_when_the_batch_write_fails()
    {
        await fixture.ResetAsync();

        // Entry points at a round that neither the batch nor the database contains:
        // the foreign key rejects it, and the cursor advance must roll back with it.
        var broken = new ProjectionResult([], [new EntrySnapshot(404, Alice, 100, T0, "0xbb", 0)]);

        await using (var db = fixture.CreateContext())
        {
            var store = new IndexerStore(db);
            await Assert.ThrowsAnyAsync<DbUpdateException>(
                () => store.CommitBatchAsync(broken, 11516209, 11516214, CancellationToken.None));
        }

        await using var read = fixture.CreateContext();
        var cursor = await read.Cursor.SingleAsync();
        Assert.Equal(11514208, cursor.LastIndexedBlock);
        Assert.Equal(0, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Loads_the_in_flight_round_so_it_survives_a_chunk_boundary()
    {
        await fixture.ResetAsync();

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 2),
                    new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0),
                    new EntrySnapshot(1, Alice, 101, T0, "0xab", 0)),
                11516209, 11516214, CancellationToken.None);

        await using var db2 = fixture.CreateContext();
        var state = await new IndexerStore(db2).LoadProjectionStateAsync(CancellationToken.None);

        Assert.Equal(1, state.LastRoundId);
        Assert.NotNull(state.ActiveRound);
        Assert.Equal(RoundStatus.Open, state.ActiveRound.Status);
        Assert.Equal(2, state.ActiveRound.EntryCount);
    }

    [Fact]
    public async Task Reports_no_active_round_once_every_round_is_settled()
    {
        await fixture.ResetAsync();
        var settled = new RoundSnapshot(1, RoundStatus.Settled, 100, T0, 110, new BigInteger(1),
            115, T0, Alice, BigInteger.One, 0);

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                new ProjectionResult([settled], []), 11516209, 11516214, CancellationToken.None);

        await using var db2 = fixture.CreateContext();
        var state = await new IndexerStore(db2).LoadProjectionStateAsync(CancellationToken.None);

        Assert.Equal(1, state.LastRoundId);
        Assert.Null(state.ActiveRound);
    }

    [Fact]
    public async Task Stores_the_entrance_fee_as_metadata()
    {
        await fixture.ResetAsync();
        var fee = BigInteger.Parse("10000000000000000");

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).SetEntranceFeeAsync(fee, CancellationToken.None);

        // Writing it twice must not violate the primary key.
        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).SetEntranceFeeAsync(fee, CancellationToken.None);

        await using var read = fixture.CreateContext();
        var row = Assert.Single(await read.Metadata.ToListAsync());
        Assert.Equal(IndexerDbContext.EntranceFeeKey, row.Key);
        Assert.Equal("10000000000000000", row.Value);
    }
}