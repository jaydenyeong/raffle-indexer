using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using System.Numerics;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class SchemaTests(PostgresFixture fixture)
{
    private static readonly BigInteger MaxUint256 = 
        BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935");

    [Fact]
    public async Task Uint256_survives_a_round_trip_without_losing_precision()
    {
        await using (var db = fixture.CreateContext())
        {
            db.Rounds.Add(new Round
            {
                Id = 9001,
                Status = RoundStatus.Settled,
                OpenedAtBlock = 1,
                OpenedAtTime = DateTimeOffset.UnixEpoch,
                RequestId = MaxUint256,
                PrizeWei = MaxUint256,
                EntryCount = 0
            });
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var round = await read.Rounds.SingleAsync(r => r.Id == 9001);

        Assert.Equal(MaxUint256, round.RequestId);
        Assert.Equal(MaxUint256, round.PrizeWei);
    }
    [Fact]
    public async Task Duplicate_tx_hash_and_log_index_is_rejected()
    {
        await using var db = fixture.CreateContext();
        db.Rounds.Add(new Round
        {
            Id = 9002,
            Status = RoundStatus.Open,
            OpenedAtBlock = 1,
            OpenedAtTime = DateTimeOffset.UnixEpoch,
            EntryCount = 0
        });
        db.Entries.Add(new Entry
        {
            RoundId = 9002,
            PlayerAddress = "0xabc",
            BlockNumber = 1,
            BlockTime = DateTimeOffset.UnixEpoch,
            TxHash = "0xdup",
            LogIndex = 0
        });
        await db.SaveChangesAsync();

        db.Entries.Add(new Entry
        {
            RoundId = 9002,
            PlayerAddress = "0xabc",
            BlockNumber = 1,
            BlockTime = DateTimeOffset.UnixEpoch,
            TxHash = "0xdup",
            LogIndex = 0
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Round_status_is_stored_as_readable_text()
    {
        await using var db = fixture.CreateContext();
        db.Rounds.Add(new Round
        {
            Id = 9003,
            Status = RoundStatus.Calculating,
            OpenedAtBlock = 1,
            OpenedAtTime = DateTimeOffset.UnixEpoch,
            EntryCount = 0
        });
        await db.SaveChangesAsync();

        // SqlQuery<T> wraps this as a subquery and projects s."Value", so the
        // column must be aliased to exactly that — quoted, or Postgres lowercases it.
        var status = await db.Database
            .SqlQuery<string>($"""SELECT status AS "Value" FROM rounds WHERE id = 9003""")
            .SingleAsync();
        
        Assert.Equal("Calculating", status);
    }
}