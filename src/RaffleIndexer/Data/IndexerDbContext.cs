using Microsoft.EntityFrameworkCore;

namespace RaffleIndexer.Data;

public class IndexerDbContext(DbContextOptions<IndexerDbContext> options) : DbContext(options)
{
    public const int CursorRowId = 1;
    public const string EntranceFeeKey = "entrance_fee_wei";
    
    public DbSet<Round> Rounds => Set<Round>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<IndexerCursor> Cursor => Set<IndexerCursor>();
    public DbSet<IndexerMetadata> Metadata => Set<IndexerMetadata>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Round>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().IsRequired();
            e.Property(x => x.Status).HasConversion<string>().IsRequired();
            e.Property(x => x.RequestId).HasColumnType("numeric(78,0)");
            e.Property(x => x.PrizeWei).HasColumnType("numeric(78,0)");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.WinnerAddress);
        });

        b.Entity<Entry>(e =>
        {
           e.HasKey(x => x.Id);
           e.HasOne(x => x.Round).WithMany(r => r.Entries).HasForeignKey(x => x.RoundId);
           e.HasIndex(x => new {x.TxHash, x.LogIndex}).IsUnique();
           e.HasIndex(x => x.PlayerAddress); 
        });

        // Without ToTable, EF names these after the DbSet properties (Cursor,
        // Metadata) and they land as bare "cursor"/"metadata" — too vague for a
        // shared schema, and not what the design doc specifies.
        b.Entity<IndexerCursor>(e =>
        {
            e.ToTable("indexer_cursor");
            e.HasKey(x => x.Id);
        });

        b.Entity<IndexerMetadata>(e =>
        {
            e.ToTable("indexer_metadata");
            e.HasKey(x => x.Key);
        });
    }
}