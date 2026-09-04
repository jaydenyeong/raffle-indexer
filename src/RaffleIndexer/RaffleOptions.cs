namespace RaffleIndexer;

public class RaffleOptions
{
    public const string SectionName = "Raffle";

    public string RpcUrl {get; set;} = "";
    public string ContractAddress {get; set;} = "0x1bf825d2a79f84c0c500f0797c5012d9724973d9";

    // Sepolia deploy block of the Raffle contract: backfill genesis.
    public long StartBlock {get; set;} = 11514209;
    
    // Turnable without a rebuild, because public RPC endpoints throttle
    public int ChunkSize {get; set;} = 2000;

    // Index only up to latestBlock - ConfirmationBlocks. Buys reorg safety
    // without rollbac machinery
    public int ConfirmationBlocks {get; set;} = 5;

    // Sepolia block time
    public int PollIntervalSeconds {get; set;} = 12;

    public bool EnableIndexer {get; set;} = true;
}