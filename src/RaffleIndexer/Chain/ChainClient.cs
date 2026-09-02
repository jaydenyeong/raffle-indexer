using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace RaffleIndexer.Chain;

public class ChainClient(IOptions<RaffleOptions> options) : IChainClient
{
    private readonly RaffleOptions _options = options.Value;
    private readonly Web3 _web3 = new(options.Value.RpcUrl);

    public async Task<long> GetLatestBlockNumberAsync(CancellationToken ct)
    {
        var block = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        return (long)block.Value;
    }

    public async Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(long fromBlock, long toBlock, CancellationToken ct)
    {
        var filter = new NewFilterInput
        {
            Address = [_options.ContractAddress],
            FromBlock = new BlockParameter(new HexBigInteger(fromBlock)),
            ToBlock = new BlockParameter(new HexBigInteger(toBlock))
        };

        var logs = await _web3.Eth.Filters.GetLogs.SendRequestAsync(filter);

        return logs
            .Select(RaffleLogDecoder.Decode)
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.BlockNumber)
            .ThenBy(e => e.LogIndex)
            .ToList();
    }
    
    public async Task<DateTimeOffset> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
    {
        var block = await _web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(blockNumber)));
        return DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value);
    }

    public async Task <BigInteger> GetBalanceAtBlockAsync(long blockNumber, CancellationToken ct)
    {
        var balance = await _web3.Eth.GetBalance.SendRequestAsync(
            _options.ContractAddress, new BlockParameter(new HexBigInteger(blockNumber))
        );
        return balance.Value;
    }

    public Task<BigInteger> GetEntranceFeeAsync(CancellationToken ct) => _web3.Eth.GetContractQueryHandler<GetEntranceFeeFunction>()
        .QueryAsync<BigInteger>(_options.ContractAddress, new GetEntranceFeeFunction());
}