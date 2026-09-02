using RaffleIndexer.Chain;
using RaffleIndexer.Data;

namespace RaffleIndexer.Indexing;

/// <summary>
/// Turns an ordered event stream into round and entry mutations.
/// <para>
/// The contract emits no round identifier, so round structure is synthesized
/// here: a round opens lazily on the first <c>RaffleEnter</c> after the
/// previous one settled, which is why an idle contract accumulates no phantom
/// rounds.
/// </para>
/// <para>
/// Depends on nothing — no chain, no database. That is what makes round
/// synthesis testable with hand-written event lists.
/// </para>
/// </summary>

public static class RaffleProjector
{
    public static ProjectionResult Project(ProjectionState state, IReadOnlyList<RaffleEvent> events)
    {
        var touched = new Dictionary<int, RoundSnapshot>();
        var entries = new List<EntrySnapshot>();

        var lastRoundId = state.LastRoundId;
        var active = state.ActiveRound;

        foreach (var e in events.OrderBy(x => x.BlockNumber).ThenBy(x => x.LogIndex))
        {
            switch (e.Kind)
            {
                case RaffleEventKind.Enter:
                    active ??= NewRound(++lastRoundId, e);
                    active = active with {EntryCount = active.EntryCount + 1};
                    entries.Add(new EntrySnapshot(
                        active.Id, e.Address!, e.BlockNumber, e.BlockTime, e.TxHash, e.LogIndex
                    ));
                    touched[active.Id] = active;
                    break;
                
                case RaffleEventKind.Requested:
                    active = Require(active, e) with
                    {
                        Status = RoundStatus.Calculating,
                        RequestedAtBlock = e.BlockNumber,
                        RequestId = e.RequestId
                    };
                    // The round stays in flight: Calculating, awaiting WinnerPicked.
                    touched[active.Id] = active;
                    break;

                case RaffleEventKind.WinnerPicked:
                    var settled = Require(active, e) with
                    {
                        Status = RoundStatus.Settled,
                        SettledAtBlock = e.BlockNumber,
                        SettledAtTime = e.BlockTime,
                        WinnerAddress = e.Address,
                        PrizeWei = e.PrizeWei
                    };
                    touched[settled.Id] = settled;
                    active = null;
                    break;
            }
        }
        return new ProjectionResult(touched.Values.OrderBy(r => r.Id).ToList(), entries);
    }
    private static RoundSnapshot NewRound(int id, RaffleEvent e) => new (
        Id: id,
        Status: RoundStatus.Open,
        OpenedAtBlock: e.BlockNumber,
        OpenedAtTime: e.BlockTime,
        RequestedAtBlock: null,
        RequestId: null,
        SettledAtBlock: null,
        SettledAtTime: null,
        WinnerAddress: null,
        PrizeWei: null,
        EntryCount: 0
    );

    private static RoundSnapshot Require(RoundSnapshot? active, RaffleEvent e) =>
        active ?? throw new InvalidOperationException(
            $"{e.Kind} at block {e.BlockNumber} log {e.LogIndex}: no round is in flight. " +
            "Projection state was not loaded, or indexing did not start at the deploy block."
        );
}