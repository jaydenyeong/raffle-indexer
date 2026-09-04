using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using RaffleIndexer.Api;
using RaffleIndexer.Data;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class EndpointTests(PostgresFixture fixture)
{
    private const string AliceLower = "0xa4e6adae4e9b607b865aada5b9811444926ff531";
    private const string AliceChecksummed = "0xa4E6ADaE4e9b607b865AaDa5b9811444926ff531";
    private const string BobLower = "0xbbbb000000000000000000000000000000000002";

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly BigInteger Prize = BigInteger.Parse("30000000000000000");

    private async Task SeedAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();

        db.Rounds.Add(new Round
        {
            Id = 1, Status = RoundStatus.Settled,
            OpenedAtBlock = 100, OpenedAtTime = T0,
            RequestedAtBlock = 110, RequestId = new BigInteger(42),
            SettledAtBlock = 115, SettledAtTime = T0.AddMinutes(5),
            WinnerAddress = AliceLower, PrizeWei = Prize, EntryCount = 2
        });
        db.Rounds.Add(new Round
        {
            Id = 2, Status = RoundStatus.Open,
            OpenedAtBlock = 200, OpenedAtTime = T0.AddHours(1), EntryCount = 1
        });

        db.Entries.Add(new Entry { RoundId = 1, PlayerAddress = AliceLower, BlockNumber = 100, BlockTime = T0, TxHash = "0xa1", LogIndex = 0 });
        db.Entries.Add(new Entry { RoundId = 1, PlayerAddress = BobLower, BlockNumber = 101, BlockTime = T0, TxHash = "0xa2", LogIndex = 0 });
        db.Entries.Add(new Entry { RoundId = 2, PlayerAddress = AliceLower, BlockNumber = 200, BlockTime = T0.AddHours(1), TxHash = "0xa3", LogIndex = 0 });

        db.Metadata.Add(new IndexerMetadata { Key = IndexerDbContext.EntranceFeeKey, Value = "10000000000000000" });
        await db.SaveChangesAsync();
    }

    private HttpClient Client() => new ApiFactory(fixture.ConnectionString).CreateClient();

    [Fact]
    public async Task Rounds_returns_newest_first_with_checksummed_addresses()
    {
        await SeedAsync();

        var page = await Client().GetFromJsonAsync<PagedResult<RoundSummary>>("/rounds");

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal([2, 1], page.Items.Select(r => r.Id));

        var settled = page.Items.Single(r => r.Id == 1);
        Assert.Equal("Settled", settled.Status);
        Assert.Equal(AliceChecksummed, settled.Winner);
        Assert.Equal("30000000000000000", settled.PrizeWei);
    }

    [Fact]
    public async Task Rounds_can_be_filtered_by_status()
    {
        await SeedAsync();

        var page = await Client().GetFromJsonAsync<PagedResult<RoundSummary>>("/rounds?status=Open");

        Assert.Equal(1, page!.TotalCount);
        Assert.Equal(2, page.Items.Single().Id);
    }

    [Fact]
    public async Task Rounds_pages()
    {
        await SeedAsync();

        var page = await Client().GetFromJsonAsync<PagedResult<RoundSummary>>("/rounds?page=2&pageSize=1");

        Assert.Equal(2, page!.Page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.Items.Single().Id);
    }

    [Fact]
    public async Task Round_detail_includes_entries_and_the_request_id()
    {
        await SeedAsync();

        var round = await Client().GetFromJsonAsync<RoundDetail>("/rounds/1");

        Assert.Equal("42", round!.RequestId);
        Assert.Equal(2, round.Entries.Count);
        Assert.Equal(AliceChecksummed, round.Entries[0].Player);
    }

    [Fact]
    public async Task Unknown_round_is_404()
    {
        await SeedAsync();

        var response = await Client().GetAsync("/rounds/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Current_round_is_the_one_still_in_flight()
    {
        await SeedAsync();

        var round = await Client().GetFromJsonAsync<RoundDetail>("/rounds/current");

        Assert.Equal(2, round!.Id);
        Assert.Equal("Open", round.Status);
    }

    [Fact]
    public async Task Player_lookup_is_case_insensitive_and_totals_winnings()
    {
        await SeedAsync();

        // Query with the checksummed form; storage is lowercase.
        var player = await Client().GetFromJsonAsync<PlayerView>($"/players/{AliceChecksummed}");

        Assert.Equal(AliceChecksummed, player!.Address);
        Assert.Equal(2, player.RoundsPlayed);
        Assert.Equal(2, player.EntryCount);
        Assert.Equal(1, player.Wins);
        Assert.Equal("30000000000000000", player.TotalWonWei);
    }

    [Fact]
    public async Task Stats_summarize_the_indexed_history()
    {
        await SeedAsync();

        var stats = await Client().GetFromJsonAsync<StatsView>("/stats");

        Assert.Equal(1, stats!.RoundsSettled);
        Assert.Equal(2, stats.RoundsTotal);
        Assert.Equal("30000000000000000", stats.TotalPaidWei);
        Assert.Equal(2, stats.UniquePlayers);
        Assert.Equal("30000000000000000", stats.BiggestPrizeWei);
        Assert.Equal("10000000000000000", stats.EntranceFeeWei);
    }

    [Fact]
    public async Task Stats_on_an_empty_database_return_zeroes_rather_than_failing()
    {
        await fixture.ResetAsync();

        var stats = await Client().GetFromJsonAsync<StatsView>("/stats");

        Assert.Equal(0, stats!.RoundsSettled);
        Assert.Equal("0", stats.TotalPaidWei);
        Assert.Null(stats.BiggestPrizeWei);
    }

    [Fact]
    public async Task Health_reports_the_cursor()
    {
        await fixture.ResetAsync();

        var health = await Client().GetFromJsonAsync<HealthView>("/health");

        Assert.Equal(11514208, health!.LastIndexedBlock);
        Assert.False(health.CaughtUp);
    }

    [Fact]
    public async Task The_OpenAPI_document_is_served()
    {
        await fixture.ResetAsync();

        var response = await Client().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
