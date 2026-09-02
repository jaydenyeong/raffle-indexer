using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;

namespace RaffleIndexer.Chain;

/// <summary>
/// Pure translation from a raw log to a normalized event. No RPC, no state —
/// which is what makes it testable against hand-written log fixtures.
/// </summary>

public static class RaffleLogDecoder
{
    public static RaffleEvent? Decode(FilterLog log)
    {
        var block = (long)log.BlockNumber.Value;
        var logIndex = (int)log.LogIndex.Value;
        var txHash = log.TransactionHash;

        if (log.IsLogForEvent<RaffleEnterEventDto>())
        {
            var decoded = log.DecodeEvent<RaffleEnterEventDto>();
            return new RaffleEvent(RaffleEventKind.Enter, block, logIndex, txHash,
                Address: Normalize(decoded.Event.Player));
        }

        if (log.IsLogForEvent<RequestedRaffleWinnerEventDto>())
        {
            var decoded = log.DecodeEvent<RequestedRaffleWinnerEventDto>();
            return new RaffleEvent(RaffleEventKind.Requested, block, logIndex, txHash,
                RequestId: decoded.Event.RequestId);
        }

        if (log.IsLogForEvent<WinnerPickedEventDto>())
        {
            var decoded = log.DecodeEvent<WinnerPickedEventDto>();
            return new RaffleEvent(RaffleEventKind.WinnerPicked, block, logIndex, txHash,
                Address: Normalize(decoded.Event.Winner));
        }

        return null;
    }

    private static string Normalize(string address) => address.ToLowerInvariant();
}