namespace RaffleIndexer.Indexing;

public static class BlockRange
{
    public static (long From, long To)? Next(
        long lastIndexedBlock, long chainHead, int chunkSize, int confirmationBlocks)
    {
        var safeHead = chainHead - confirmationBlocks;
        var from = lastIndexedBlock + 1;

        if (from > safeHead) return null;
         var to = Math.Min(from + chunkSize - 1, safeHead);
         return(from , to);
    }
}