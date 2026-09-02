using System.Numerics;
using RaffleIndexer.Data;

namespace RaffleIndexer.Indexing;

public sealed record RoundSnapshot(
    int Id,
    RoundStatus Status,
    long OpenedAtBlock,
    DateTimeOffset OpenedAtTime,
    long? RequestedAtBlock,
    BigInteger? RequestId,
    long? SettledAtBlock,
    DateTimeOffset? SettledAtTime,
    string? WinnerAddress,
    BigInteger? PrizeWei,
    int EntryCount
);

public sealed record EntrySnapshot(
    int RoundId,
    string PlayerAddress,
    long BlockNumber,
    DateTimeOffset BlockTime,
    string TxHash,
    int LogIndex
);

public sealed record ProjectionState(int LastRoundId, RoundSnapshot? ActiveRound)
{
    public static ProjectionState Empty {get;} = new(0, null);
}

public sealed record ProjectionResult(
    IReadOnlyList<RoundSnapshot> TouchedRounds,
    IReadOnlyList<EntrySnapshot> NewEntries
);
