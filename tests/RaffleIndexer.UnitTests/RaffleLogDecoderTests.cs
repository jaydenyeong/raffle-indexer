using System.Numerics;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using RaffleIndexer.Chain;

namespace RaffleIndexer.UnitTests;

public class RaffleLogDecoderTests
{
    private const string EnterTopic =
        "0x0805e1d667bddb8a95f0f09880cf94f403fb596ce79928d9f29b74203ba284d4";
    private const string RequestedTopic = 
        "0xcd6e45c8998311cab7e9d4385596cac867e20a0587194b954fa3a731c93ce78b";
    private const string WinnerTopic =
        "0x5b690ec4a06fe979403046eaeea5b3ce38524683c3001f662c8b5a829632f7df";
    
    private static FilterLog Log(string topic0, string topic1, long block = 11514300, int logIndex = 2) =>
        new()
        {
            Address = "0x1bf825d2a79f84c0c500f0797c5012d9724973d9",
            Topics = [topic0, topic1],
            Data = "0x",
            BlockNumber = new HexBigInteger(block),
            LogIndex = new HexBigInteger(logIndex),
            TransactionHash = "0xfeed"
        };
    
    [Fact]
    public void Decodes_RaffleEnter_and_lowercases_the_player_address()
    {
        var log = Log(EnterTopic,
            "0x000000000000000000000000a4e6adae4e9b607b865aada5b9811444926ff531");

        var evt = RaffleLogDecoder.Decode(log);

        Assert.NotNull(evt);
        Assert.Equal(RaffleEventKind.Enter, evt.Kind);
        Assert.Equal("0xa4e6adae4e9b607b865aada5b9811444926ff531", evt.Address);
        Assert.Equal(11514300, evt.BlockNumber);
        Assert.Equal(2, evt.LogIndex);
        Assert.Equal("0xfeed", evt.TxHash);
    }

    [Fact]
    public void Decodes_RequestedRaffleWinner_request_id()
    {
        var log = Log(RequestedTopic,
        "0x00000000000000000000000000000000000000000000000000000000000004d2");

        var evt = RaffleLogDecoder.Decode(log);

        Assert.NotNull(evt);
        Assert.Equal(RaffleEventKind.Requested, evt.Kind);
        Assert.Equal(new BigInteger(1234), evt.RequestId);
        Assert.Null(evt.Address);
    }

    [Fact]
    public void Decodes_a_full_width_request_id_without_losing_precision()
    {
        var log = Log(RequestedTopic,
            "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        var evt = RaffleLogDecoder.Decode(log);

        Assert.Equal(BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935"),
        evt!.RequestId);
    }

    [Fact]
    public void Decodes_WinnerPicked_and_lowercases_the_winner_address()
    {
        var log = Log(WinnerTopic, "0x000000000000000000000000a4e6adae4e9b607b865aada5b9811444926ff531");

        var evt = RaffleLogDecoder.Decode(log);

        Assert.NotNull(evt);
        Assert.Equal(RaffleEventKind.WinnerPicked, evt.Kind);
        Assert.Equal("0xa4e6adae4e9b607b865aada5b9811444926ff531", evt.Address);
    }

    [Fact]
    public void Returns_null_for_an_unrelated_log()
    {
        var log = Log("0x1111111111111111111111111111111111111111111111111111111111111111",
        "0x0000000000000000000000000000000000000000000000000000000000000000");

        Assert.Null(RaffleLogDecoder.Decode(log));
    }
}