using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace RaffleIndexer.Chain;

[Event("RaffleEnter")]
public class RaffleEnterEventDto : IEventDTO
{
    [Parameter("address", "player", 1, true)]
    public string Player {get; set;} = "";
}

/// <summary>
/// The entry event as the deployed Sepolia contract declares it. That
/// deployment predates a rename in src/Raffle.sol, which now emits
/// <see cref="RaffleEnterEventDto"/>. Both are indexed as an entry so the same
/// build works against the live contract and against a freshly deployed one.
/// </summary>
[Event("EnteredRaffle")]
public class EnteredRaffleEventDto : IEventDTO
{
    [Parameter("address", "player", 1, true)]
    public string Player { get; set; } = "";
}

[Event("RequestedRaffleWinner")]
public class RequestedRaffleWinnerEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)]
    public BigInteger RequestId {get; set;}
}

[Event("WinnerPicked")]
public class WinnerPickedEventDto : IEventDTO
{
    [Parameter("address", "winner", 1, true)]
    public string Winner {get; set;} = "";
}

[Function("getEntranceFee", "uint256")]
public class GetEntranceFeeFunction : FunctionMessage;