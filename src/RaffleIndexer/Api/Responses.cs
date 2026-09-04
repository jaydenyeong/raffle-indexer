namespace RaffleIndexer.Api;

public sealed record RoundSummary(
    int Id,
    string Status,
    long OpenedAtBlock,
    DateTimeOffset OpenedAtTime,
    long? SettledAtBlock,
    DateTimeOffset? SettledAtTime,
    string? Winner,
    string? PrizeWei,
    int EntryCount
);

public sealed record EntryView(
    string Player,
    long BlockNumber,
    DateTimeOffset BlockTime,
    string TxHash,
    int LogIndex
);

public sealed record RoundDetail(
    int Id,
    string Status,
    long OpenedAtBlock,
    DateTimeOffset OpenedAtTime,
    long? RequestedAtBlock,
    string? RequestId,
    long? SettledAtBlock,
    DateTimeOffset? SettledAtTime,
    string? Winner,
    string? PrizeWei,
    int EntryCount,
    IReadOnlyList<EntryView> Entries
);

public sealed record PagedResult<T>(int Page, int PageSize, int TotalCount, IReadOnlyList<T> Items);

public sealed record PlayerView(
    string Address,
    int RoundsPlayed,
    int EntryCount,
    int Wins,
    string TotalWonWei,
    IReadOnlyList<EntryView> Entries
);

public sealed record StatsView(
    int RoundsSettled,
    int RoundsTotal,
    string TotalPaidWei,
    int UniquePlayers,
    string? BiggestPrizeWei,
    string? EntranceFeeWei
);

public sealed record HealthView(
    long LastIndexedBlock,
    long ChainHeadBlock,
    long BlocksBehind,
    bool CaughtUp,
    DateTimeOffset UpdatedAt
);