using System.Numerics;

namespace RaffleIndexer.Chain;

public enum RaffleEventKind
{
    Enter,
    Requested,
    WinnerPicked
}

/// <summary>
/// One decoded contract event, normalized across the three event types.
/// <para>
/// The chain client fills everything except <see cref="BlockTime"/> and
/// <see cref="PrizeWei"/>; those need extra RPC calls, so the worker enriches
/// them before handing the event to the projector.
/// </para>
/// </summary>
/// 

public sealed record RaffleEvent(
    RaffleEventKind Kind,
    long BlockNumber,
    int LogIndex,
    string TxHash,
    string? Address = null,
    BigInteger? RequestId = null)
{
    public DateTimeOffset BlockTime {get; init;}
    public BigInteger? PrizeWei {get; init;}
}