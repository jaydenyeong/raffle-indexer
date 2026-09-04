using RaffleIndexer.Indexing;

namespace RaffleInxdexer.UnitTests;

public class BlockRangeTests
{
    [Fact]
    public void First_backfill_chunk_starts_at_the_block_after_the_cursor()
    {
        var range = BlockRange.Next(lastIndexedBlock: 11514208, chainHead: 12000000,
            chunkSize: 2000, confirmationBlocks: 5);
        
        Assert.NotNull(range);
        Assert.Equal(11514209, range.Value.From);
        Assert.Equal(11516208, range.Value.To);
    }

    [Fact]
    public void A_chunk_is_capped_at_chunkSize_blocks()
    {
        var range = BlockRange.Next(0, 12000000, chunkSize: 2000, confirmationBlocks: 5);

        Assert.Equal(1, range!.Value.From);
        Assert.Equal(2000, range.Value.To);
        Assert.Equal(2000, range.Value.To - range.Value.From + 1);
    }

    [Fact]
    public void Never_indexes_inside_the_confirmation_window()
    {
        var range = BlockRange.Next(lastIndexedBlock: 11999000, chainHead: 12000000,
            chunkSize: 2000, confirmationBlocks: 5);

            Assert.Equal(11999995, range!.Value.To);
    }

    [Fact]
    public void Returns_null_when_the_cursor_has_reached_the_safe_head()
    {
        Assert.Null(BlockRange.Next(lastIndexedBlock: 11999995, chainHead: 12000000,
            chunkSize: 2000, confirmationBlocks: 5));
    }

    [Fact]
    public void Returns_null_when_the_cursor_sits_inside_the_confirmation_window()
    {
        Assert.Null(BlockRange.Next(lastIndexedBlock: 11999999, chainHead: 12000000,
            chunkSize: 2000, confirmationBlocks: 5));
    }

    [Fact]
    public void Returns_null_on_a_chain_shorter_than_the_confirmation_window()
    {
        Assert.Null(BlockRange.Next(lastIndexedBlock: 0, chainHead: 3,
            chunkSize: 2000, confirmationBlocks: 5));
    }

    [Fact]
    public void Zero_confirmations_indexes_right_up_to_the_head()
    {
        var range = (BlockRange.Next(lastIndexedBlock: 5, chainHead: 9,
            chunkSize: 2000, confirmationBlocks: 0));

        Assert.Equal(6, range!.Value.From);
        Assert.Equal(9, range.Value.To);
    }
}