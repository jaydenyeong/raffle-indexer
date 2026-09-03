using System.Numerics;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.Data;

public interface IIndexerStore
{
    Task<long> GetLastIndexedBlockAsync(CancellationToken ct);

    Task<ProjectionState> LoadProjectionStateAsync(CancellationToken ct);

    Task CommitBatchAsync(ProjectionResult result, long LastIndexedBlock, long chainHead, CancellationToken ct);

    Task SetEntranceFeeAsync(BigInteger entranceFeeWei, CancellationToken ct);
}