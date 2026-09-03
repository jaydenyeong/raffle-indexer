using System.Numerics;

namespace RaffleIndexer.Data;

public enum RoundStatus
{
    Open,
    Calculating,
    Settled
}

public class Round
{
    public int Id {get; set;}
    public RoundStatus Status {get; set;}

    public long OpenedAtBlock {get; set;}
    public DateTimeOffset OpenedAtTime {get; set;}

    public long? RequestedAtBlock {get; set;}
    public BigInteger? RequestId {get; set;}

    public long? SettledAtBlock {get; set;}
    public DateTimeOffset? SettledAtTime {get; set;}

    public string? WinnerAddress {get; set;}
    public BigInteger? PrizeWei {get; set;}

    public int EntryCount {get; set;}

    public List<Entry> Entries {get; set;} = [];
}

public class Entry
{
    public long Id {get; set;}
    public int RoundId {get; set;}
    public Round? Round {get; set;}

    public string PlayerAddress {get; set;} = "";
    public long BlockNumber {get; set;}
    public DateTimeOffset BlockTime {get; set;}
    public string TxHash {get; set;} = "";
    public int LogIndex {get; set;}
}

public class IndexerCursor
{
    public int Id {get; set;}
    public long LastIndexedBlock {get; set;}
    public long ChainHeadBlock {get; set;}
    public DateTimeOffset UpdatedAt {get; set;}
}

public class IndexerMetadata
{
    public string Key {get; set;} = "";
    public string Value {get; set;} = "";
}
