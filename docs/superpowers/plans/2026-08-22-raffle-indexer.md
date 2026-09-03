# Raffle Indexer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET Web API that indexes the Sepolia `Raffle` contract's events into Postgres and serves round/player/stats queries from the database, running under Docker Compose.

**Architecture:** A single ASP.NET Core app hosts both a Minimal API (read-only, Postgres-only) and a `BackgroundService` indexer. The indexer polls Sepolia via Nethereum in block chunks, enriches raw logs with block timestamps and contract balances, feeds them to a pure `RaffleProjector` that synthesizes round structure the contract never emits, then writes rows and advances the cursor in one transaction. Compose runs the app alongside Postgres 17.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core Minimal API, EF Core 10 + Npgsql 10, Nethereum.Web3 6.1.0, Scalar.AspNetCore, xunit, Testcontainers.PostgreSql, Docker Compose, Postgres 17.

**Spec:** [docs/superpowers/specs/2026-08-20-raffle-indexer-design.md](../specs/2026-08-20-raffle-indexer-design.md)

## Global Constraints

Every task's requirements implicitly include this section.

| Constraint | Exact value |
|---|---|
| Contract address | `0x1bf825d2a79f84c0c500f0797c5012d9724973d9` |
| Chain | Sepolia, chain id `11155111` |
| Deploy block (backfill genesis) | `11514209` |
| .NET SDK | `10.0.300`; target framework `net10.0` |
| Backfill chunk size | 2,000 blocks (configurable) |
| Confirmation lag | 5 blocks — never index past `latestBlock - 5` |
| Tail poll interval | 12 seconds (configurable) |
| uint256 storage | `System.Numerics.BigInteger` → `numeric(78,0)`. **Never `decimal`** — it holds ~29 significant digits, uint256 needs 78. |
| Address storage | lowercase in the DB; EIP-55 checksummed only in API responses |
| Idempotency key | `UNIQUE (tx_hash, log_index)` on `entries` |
| API ↔ chain | **The API never touches the chain.** Endpoints read Postgres only. No exceptions. |
| Writes to chain | None, ever. No transaction sending, no private keys. |
| Round ids | Assigned by the projector, not a DB identity column — round numbers must be reproducible on replay |

### Event signatures (verified with `cast keccak`)

| Event | topic0 |
|---|---|
| `RaffleEnter(address)` | `0x0805e1d667bddb8a95f0f09880cf94f403fb596ce79928d9f29b74203ba284d4` |
| `RequestedRaffleWinner(uint256)` | `0xcd6e45c8998311cab7e9d4385596cac867e20a0587194b954fa3a731c93ce78b` |
| `WinnerPicked(address)` | `0x5b690ec4a06fe979403046eaeea5b3ce38524683c3001f662c8b5a829632f7df` |

### Verified findings that amend the spec

Three things were checked against the real toolchain while writing this plan. Where they contradict the spec, **this plan wins**:

1. **No EF value converter is needed for uint256.** The spec says `BigInteger` maps to `numeric(78,0)` "via an EF value converter". It does not need one: Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 ships `NpgsqlBigIntegerTypeMapping` natively. Declaring the property as `BigInteger` with `.HasColumnType("numeric(78,0)")` is sufficient. Task 2 proves this with a round-trip test of 2^256−1.
2. **Nethereum returns indexed addresses already EIP-55 checksummed.** `DecodeEvent<T>()` on a `RaffleEnter` log yields `0xa4E6ADaE4e9b607b865AaDa5b9811444926ff531`, mixed case. Every decoded address must be `.ToLowerInvariant()`-ed before it reaches the database, or the lowercase-storage constraint silently breaks.
3. **`/health` cannot call the chain.** The spec wants `/health` to report the chain head, but the API is forbidden from touching RPC. Resolution: the worker persists the latest observed head into the cursor row as `chain_head_block` on every commit, and `/health` reads it from Postgres like everything else.

Two smaller deviations, for the same reason:

4. **The lottery repo has no `EnterRaffle` forge script.** `script/Interactions.s.sol` contains only `CreateSubscription`, `AddConsumer`, and `FundSubscription`. Task 9 uses `cast send` to fire `enterRaffle` against Anvil instead of adding a script to a separate repo.
5. **The Dockerfile copy paths differ from the spec sketch.** The spec assumes a flat root (`COPY *.csproj .`); this plan puts the app in `src/RaffleIndexer/`, so the paths are explicit. The layer ordering the spec cares about — restore before source copy — is preserved exactly.
6. **`entry_count` is recomputed from the entries table on every commit, not carried as a running total.** The spec calls it denormalized, which it stays — but a replayed chunk dedupes its entry inserts while the projector's incremented count survives, so a pure running total drifts above the real row count. Task 6 recomputes it inside the same transaction, which makes the column self-healing and the replay a genuine no-op.

### Conventions for every task

- **Assertions:** plain xunit `Assert`. Do not add FluentAssertions — v8+ is commercially licensed.
- **Snake-case columns:** `EFCore.NamingConventions` (`.UseSnakeCaseNamingConvention()`) maps `LastIndexedBlock` → `last_indexed_block` automatically. Do not hand-write `HasColumnName` for every property.
- **Commit at the end of every task**, using the message given in that task's final step.
- Run all `dotnet` commands from the repo root, `d:\VSCode\raffle-indexer`.

## File Structure

```
raffle-indexer/
├── RaffleIndexer.sln
├── Dockerfile                       multi-stage build; runtime image runs as $APP_UID
├── .dockerignore                    keeps host bin/obj/.git out of the build context
├── docker-compose.yml               api + db, named volume, healthcheck gate
├── .env.example                     documents SEPOLIA_RPC_URL / POSTGRES_PASSWORD
├── README.md                        how to run it (Task 9)
├── docs/superpowers/{specs,plans}/
├── src/RaffleIndexer/
│   ├── RaffleIndexer.csproj
│   ├── Program.cs                   composition root; endpoint mapping; migrate-on-start
│   ├── RaffleOptions.cs             bound config: RpcUrl, ContractAddress, StartBlock, …
│   ├── Chain/
│   │   ├── RaffleEvent.cs           normalized event record (Kind + block + payload)
│   │   ├── EventDtos.cs             Nethereum [Event] DTOs + getEntranceFee [Function] DTO
│   │   ├── RaffleLogDecoder.cs      pure FilterLog → RaffleEvent?; unit-testable, no RPC
│   │   ├── IChainClient.cs          the only interface the worker knows about
│   │   └── ChainClient.cs           Nethereum implementation of IChainClient
│   ├── Data/
│   │   ├── IndexerDbContext.cs      DbSets + model configuration
│   │   ├── Entities.cs              Round, Entry, IndexerCursor, IndexerMetadata
│   │   ├── IIndexerStore.cs         load projection state / commit a batch atomically
│   │   ├── IndexerStore.cs          EF implementation; owns the transaction
│   │   └── Migrations/              generated by dotnet-ef
│   ├── Indexing/
│   │   ├── ProjectionTypes.cs       RoundStatus, RoundSnapshot, EntrySnapshot, state/result
│   │   ├── RaffleProjector.cs       pure: (state, events) → mutations. Depends on nothing.
│   │   └── IndexerWorker.cs         BackgroundService: cursor, chunking, enrichment, commit
│   └── Api/
│       ├── Responses.cs             response records (checksummed addresses, wei as string)
│       ├── AddressFormatting.cs     lowercase → EIP-55 for the response layer
│       ├── RoundEndpoints.cs        /rounds, /rounds/{id}, /rounds/current
│       └── MiscEndpoints.cs         /players/{address}, /stats, /health
└── tests/
    ├── RaffleIndexer.UnitTests/     decoder + projector; no Docker, no network
    └── RaffleIndexer.IntegrationTests/   Testcontainers Postgres; store + endpoints
```

Rationale for the split: `Chain/`, `Indexing/`, `Data/`, and `Api/` change for different reasons and can be reviewed independently. `RaffleProjector` sits alone in a file with no dependencies on the other three folders — that isolation is the whole reason the round-synthesis logic is cheap to test.

---

### Task 1: Container loop before any chain code

Get `docker compose up` serving a stub `/health` before a single line of blockchain or database code exists. When something breaks later, this task's output is the known-good baseline.

**Files:**
- Create: `RaffleIndexer.sln`
- Create: `src/RaffleIndexer/RaffleIndexer.csproj`, `src/RaffleIndexer/Program.cs`
- Create: `Dockerfile`, `.dockerignore`, `docker-compose.yml`, `.env.example`

**Interfaces:**
- Consumes: nothing.
- Produces: a container listening on host port `8080`; `GET /health` → `200 {"status":"starting"}`. Later tasks replace the body of `/health` but keep the route and port.

- [ ] **Step 1: Scaffold the solution and web project**

```bash
dotnet new sln -n RaffleIndexer
dotnet new web -o src/RaffleIndexer -f net10.0
dotnet sln add src/RaffleIndexer/RaffleIndexer.csproj
```

- [ ] **Step 2: Write the stub Program.cs**

Replace the generated `src/RaffleIndexer/Program.cs` entirely:

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "starting" }));

app.Run();
```

- [ ] **Step 3: Verify it runs on the host**

Run: `dotnet run --project src/RaffleIndexer`
Then in a second shell: `curl http://localhost:5000/health` (use the port the console prints).
Expected: `{"status":"starting"}`. Stop the app with Ctrl+C.

- [ ] **Step 4: Write the .dockerignore**

Create `.dockerignore`. Without this, host `bin/` and `obj/` are copied into the build context and shadow the container's own build output — the resulting failures are genuinely baffling, so this file comes first.

```
bin/
obj/
**/bin/
**/obj/
.git
.env
docs/
```

- [ ] **Step 5: Write the multi-stage Dockerfile**

Create `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project file first and restore. This layer stays cached until a
# dependency changes, so editing a .cs file does not re-download every package.
COPY src/RaffleIndexer/RaffleIndexer.csproj src/RaffleIndexer/
RUN dotnet restore src/RaffleIndexer/RaffleIndexer.csproj

COPY src/ src/
RUN dotnet publish src/RaffleIndexer/RaffleIndexer.csproj -c Release -o /app --no-restore

# Runtime-only image: no SDK, no compilers, roughly half the size.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "RaffleIndexer.dll"]
```

- [ ] **Step 6: Write docker-compose.yml**

Create `docker-compose.yml`. The `db` service is defined now even though nothing connects to it yet — bringing it up early is what makes the healthcheck gate and the named volume observable in the next task.

```yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_USER: indexer
      POSTGRES_DB: indexer
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U indexer -d indexer"]
      interval: 5s
      timeout: 5s
      retries: 10

  api:
    build: .
    depends_on:
      db:
        # Plain depends_on waits only for the container to START, which is why so
        # many apps crash on their first boot against Postgres.
        condition: service_healthy
    environment:
      ConnectionStrings__Default: "Host=db;Database=indexer;Username=indexer;Password=${POSTGRES_PASSWORD}"
      Raffle__RpcUrl: ${SEPOLIA_RPC_URL}
    ports:
      - "8080:8080"

volumes:
  pgdata:
```

`Host=db` resolves because Compose created a network and registered each service under its own name.

- [ ] **Step 7: Write .env.example and create a local .env**

Create `.env.example` (committed):

```
# Copy to .env and fill in. .env is gitignored and loaded automatically by Compose.
SEPOLIA_RPC_URL=https://eth-sepolia.g.alchemy.com/v2/YOUR_KEY
POSTGRES_PASSWORD=change-me
```

Then create the real `.env` (already covered by the existing `.gitignore`):

```bash
cp .env.example .env
```

Edit `.env` and set `POSTGRES_PASSWORD` to any value. `SEPOLIA_RPC_URL` may stay as the placeholder until Task 4 — nothing reads it before then.

- [ ] **Step 8: Bring the stack up and hit /health through the container**

```bash
docker compose up --build -d
docker compose ps
curl http://localhost:8080/health
```

Expected: `docker compose ps` shows `db` as `healthy` and `api` as `running`; the curl returns `{"status":"starting"}`.
If `api` exited, read `docker compose logs api` before changing anything.

- [ ] **Step 9: Prove the named volume outlives the containers**

```bash
docker compose down
docker volume ls | grep pgdata
docker compose up -d
```

Expected: `docker volume ls` still lists the `pgdata` volume after `down` — data survives. (`docker compose down -v` would remove it; do not run that.)

- [ ] **Step 10: Commit**

```bash
git add .dockerignore Dockerfile docker-compose.yml .env.example RaffleIndexer.sln src/
git commit -m "feat: scaffold web app and Docker Compose stack with stub /health"
```

---

### Task 2: Schema, uint256 storage, and the Testcontainers harness

The database schema and the proof that a full-width uint256 survives a round trip. Schema only — Task 3 wires it into the running app.

**Files:**
- Create: `src/RaffleIndexer/Data/Entities.cs`, `src/RaffleIndexer/Data/IndexerDbContext.cs`
- Create: `src/RaffleIndexer/Data/Migrations/` (generated)
- Create: `src/RaffleIndexer/appsettings.json` (replace the generated one)
- Create: `tests/RaffleIndexer.IntegrationTests/RaffleIndexer.IntegrationTests.csproj`, `tests/RaffleIndexer.IntegrationTests/PostgresFixture.cs`, `tests/RaffleIndexer.IntegrationTests/SchemaTests.cs`
- Modify: `src/RaffleIndexer/RaffleIndexer.csproj`, `src/RaffleIndexer/Program.cs`, `RaffleIndexer.sln`

**Interfaces:**
- Consumes: the project from Task 1.
- Produces:
  - `RaffleIndexer.Data.RoundStatus` — enum `Open | Calculating | Settled`, stored as text.
  - `RaffleIndexer.Data.Round`, `.Entry`, `.IndexerCursor`, `.IndexerMetadata` entity classes (exact properties below).
  - `RaffleIndexer.Data.IndexerDbContext` with `DbSet<Round> Rounds`, `DbSet<Entry> Entries`, `DbSet<IndexerCursor> Cursor`, `DbSet<IndexerMetadata> Metadata`, and constants `CursorRowId = 1`, `EntranceFeeKey = "entrance_fee_wei"`.
  - `RaffleIndexer.IntegrationTests.PostgresFixture` — xunit collection fixture exposing `string ConnectionString` and `IndexerDbContext CreateContext()`; collection name `PostgresCollection`.

- [ ] **Step 1: Add the data packages**

```bash
dotnet add src/RaffleIndexer package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/RaffleIndexer package Microsoft.EntityFrameworkCore.Design
dotnet add src/RaffleIndexer package EFCore.NamingConventions

# Both of these are required. See the note below — without them the test
# projects in Tasks 2, 4, and 6 fail to build with CS1705.
dotnet add src/RaffleIndexer package Microsoft.EntityFrameworkCore --version 10.0.11
dotnet add src/RaffleIndexer package Microsoft.EntityFrameworkCore.Relational --version 10.0.11
```

Expected: the Npgsql provider resolves to 10.0.3 and NamingConventions to 10.0.x.

**Why the last two lines exist.** `Microsoft.EntityFrameworkCore.Design` carries
`PrivateAssets="all"`, so the EF version it pulls in (10.0.11) does *not* flow
across a `ProjectReference`. A test project referencing this app would resolve
EF from the only constraint that does reach it — `Npgsql.EntityFrameworkCore.PostgreSQL
10.0.3`, whose floor is **10.0.4** — and then fail to compile against an app
assembly built on 10.0.11:

```
error CS1705: Assembly 'RaffleIndexer' uses 'Microsoft.EntityFrameworkCore,
Version=10.0.11.0' which has a higher version than referenced assembly
'Microsoft.EntityFrameworkCore' with identity '...Version=10.0.4.0'
```

Declaring both packages directly and non-privately fixes it at the source: the
app genuinely compiles against both (`DbContext` from EF Core; `HasColumnType`,
`MigrateAsync`, and `BeginTransactionAsync` from Relational), and a non-private
reference propagates its version to every consuming project. Pin both to the
same version — mismatching them reintroduces the conflict on the other assembly.

If the two versions ever drift again as packages are added, `Directory.Packages.props`
(Central Package Management) is the heavier but permanent answer.

- [ ] **Step 2: Install the EF tooling**

```bash
dotnet tool install --global dotnet-ef
dotnet ef --version
```

Expected: version 10.x. If it is already installed at an older major, run `dotnet tool update --global dotnet-ef`.

- [ ] **Step 3: Write the entities**

Create `src/RaffleIndexer/Data/Entities.cs`:

```csharp
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
    // Assigned by the projector, never by the database: round numbers must be
    // reproducible if history is replayed from scratch.
    public int Id { get; set; }
    public RoundStatus Status { get; set; }

    public long OpenedAtBlock { get; set; }
    public DateTimeOffset OpenedAtTime { get; set; }

    public long? RequestedAtBlock { get; set; }
    public BigInteger? RequestId { get; set; }

    public long? SettledAtBlock { get; set; }
    public DateTimeOffset? SettledAtTime { get; set; }

    public string? WinnerAddress { get; set; }
    public BigInteger? PrizeWei { get; set; }

    // Denormalized so round list queries need no join or subquery.
    public int EntryCount { get; set; }

    public List<Entry> Entries { get; set; } = [];
}

public class Entry
{
    public long Id { get; set; }
    public int RoundId { get; set; }
    public Round? Round { get; set; }

    public string PlayerAddress { get; set; } = "";
    public long BlockNumber { get; set; }
    public DateTimeOffset BlockTime { get; set; }
    public string TxHash { get; set; } = "";
    public int LogIndex { get; set; }
}

public class IndexerCursor
{
    // Single row, always id 1.
    public int Id { get; set; }

    // Highest block whose events are fully committed.
    public long LastIndexedBlock { get; set; }

    // Latest chain head the worker observed. Persisted so /health can report
    // "blocks behind" without the API ever calling RPC.
    public long ChainHeadBlock { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class IndexerMetadata
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
```

- [ ] **Step 4: Write the DbContext**

Create `src/RaffleIndexer/Data/IndexerDbContext.cs`:

```csharp
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
            e.Property(x => x.RequestId).HasColumnType("numeric(78,0)");
            e.Property(x => x.PrizeWei).HasColumnType("numeric(78,0)");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.WinnerAddress);
        });

        b.Entity<Entry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Round).WithMany(r => r.Entries).HasForeignKey(x => x.RoundId);
            // Idempotency key: a replayed batch collides instead of duplicating.
            e.HasIndex(x => new { x.TxHash, x.LogIndex }).IsUnique();
            e.HasIndex(x => x.PlayerAddress);
        });

        // ToTable is required. Without it EF names these after the DbSet
        // properties (Cursor, Metadata), so they land as bare "cursor" and
        // "metadata" rather than the names the design doc specifies.
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
```

`RequestId` and `PrizeWei` are plain `BigInteger` properties with no value converter — Npgsql 10 has a built-in `NpgsqlBigIntegerTypeMapping`, and `numeric(78,0)` holds all 78 digits of a uint256 exactly. Step 8 proves it.

- [ ] **Step 5: Write appsettings.json with a design-time connection string**

Replace `src/RaffleIndexer/appsettings.json` so `dotnet ef` can build the model (no live database is contacted for `migrations add`):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=indexer;Username=indexer;Password=postgres"
  }
}
```

- [ ] **Step 6: Register the DbContext so the EF tools can find it**

In `src/RaffleIndexer/Program.cs`, insert above `var app = builder.Build();`:

```csharp
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;

builder.Services.AddDbContext<IndexerDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
     .UseSnakeCaseNamingConvention());
```

(`using` directives go at the top of the file, above `var builder = ...`.)

- [ ] **Step 7: Generate the initial migration**

```bash
dotnet ef migrations add InitialSchema -p src/RaffleIndexer -s src/RaffleIndexer -o Data/Migrations
```

Expected: `src/RaffleIndexer/Data/Migrations/*_InitialSchema.cs` appears. Open it and confirm `rounds` has `request_id` and `prize_wei` typed `numeric(78,0)`, and `entries` has a unique index on `(tx_hash, log_index)`. If those columns came out as bare `numeric` or as `decimal`, the `HasColumnType` calls in Step 4 are wrong — fix and regenerate before continuing.

- [ ] **Step 8: Write the schema tests**

```bash
dotnet new xunit -o tests/RaffleIndexer.IntegrationTests -f net10.0
dotnet sln add tests/RaffleIndexer.IntegrationTests/RaffleIndexer.IntegrationTests.csproj
dotnet add tests/RaffleIndexer.IntegrationTests reference src/RaffleIndexer/RaffleIndexer.csproj
dotnet add tests/RaffleIndexer.IntegrationTests package Testcontainers.PostgreSql

# The template's empty UnitTest1.cs asserts nothing but counts as a passing
# test, inflating every test-count check in this plan.
rm tests/RaffleIndexer.IntegrationTests/UnitTest1.cs
```

Create `tests/RaffleIndexer.IntegrationTests/PostgresFixture.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using Testcontainers.PostgreSql;

namespace RaffleIndexer.IntegrationTests;

public class PostgresFixture : IAsyncLifetime
{
    // Testcontainers 4.14+ deprecated the parameterless constructor; the image
    // goes to the constructor, not .WithImage() (which warns CS0618).
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public IndexerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IndexerDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IndexerDbContext(options);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

Create `tests/RaffleIndexer.IntegrationTests/SchemaTests.cs`:

```csharp
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class SchemaTests(PostgresFixture fixture)
{
    private static readonly BigInteger MaxUint256 =
        BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935");

    [Fact]
    public async Task Uint256_survives_a_round_trip_without_losing_precision()
    {
        await using (var db = fixture.CreateContext())
        {
            db.Rounds.Add(new Round
            {
                Id = 9001,
                Status = RoundStatus.Settled,
                OpenedAtBlock = 1,
                OpenedAtTime = DateTimeOffset.UnixEpoch,
                RequestId = MaxUint256,
                PrizeWei = MaxUint256,
                EntryCount = 0
            });
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var round = await read.Rounds.SingleAsync(r => r.Id == 9001);

        Assert.Equal(MaxUint256, round.RequestId);
        Assert.Equal(MaxUint256, round.PrizeWei);
    }

    [Fact]
    public async Task Duplicate_tx_hash_and_log_index_is_rejected()
    {
        await using var db = fixture.CreateContext();
        db.Rounds.Add(new Round
        {
            Id = 9002,
            Status = RoundStatus.Open,
            OpenedAtBlock = 1,
            OpenedAtTime = DateTimeOffset.UnixEpoch,
            EntryCount = 0
        });
        db.Entries.Add(new Entry
        {
            RoundId = 9002,
            PlayerAddress = "0xabc",
            BlockNumber = 1,
            BlockTime = DateTimeOffset.UnixEpoch,
            TxHash = "0xdup",
            LogIndex = 0
        });
        await db.SaveChangesAsync();

        db.Entries.Add(new Entry
        {
            RoundId = 9002,
            PlayerAddress = "0xabc",
            BlockNumber = 1,
            BlockTime = DateTimeOffset.UnixEpoch,
            TxHash = "0xdup",
            LogIndex = 0
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Round_status_is_stored_as_readable_text()
    {
        await using var db = fixture.CreateContext();
        db.Rounds.Add(new Round
        {
            Id = 9003,
            Status = RoundStatus.Calculating,
            OpenedAtBlock = 1,
            OpenedAtTime = DateTimeOffset.UnixEpoch,
            EntryCount = 0
        });
        await db.SaveChangesAsync();

        // SqlQuery<T> wraps this as a subquery and projects s."Value", so the
        // column must be aliased to exactly that — quoted, or Postgres lowercases it.
        var status = await db.Database
            .SqlQuery<string>($"""SELECT status AS "Value" FROM rounds WHERE id = 9003""")
            .SingleAsync();

        Assert.Equal("Calculating", status);
    }
}
```

These tests come after the schema rather than before it because a database schema has no meaningful "minimal implementation" — the schema *is* the unit. They still gate the task.

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/RaffleIndexer.IntegrationTests`
Expected: PASS, 3 tests. The first run pulls `postgres:17`, which can take a minute. A failure about `BigInteger` having no type mapping, or a precision mismatch in the first test, points at the `HasColumnType` declarations in Step 4.

- [ ] **Step 10: Commit**

```bash
git add src/RaffleIndexer tests/RaffleIndexer.IntegrationTests RaffleIndexer.sln
git commit -m "feat: add EF schema with exact uint256 storage and Testcontainers harness"
```

---

### Task 3: Wire the database into the app

Migrations run at startup, the cursor row is seeded, and `/health` reports real values from Postgres. After this task the two containers are genuinely talking to each other.

**Files:**
- Create: `src/RaffleIndexer/RaffleOptions.cs`
- Modify: `src/RaffleIndexer/Program.cs`, `docker-compose.yml`

**Interfaces:**
- Consumes: `IndexerDbContext`, `IndexerCursor` from Task 2.
- Produces:
  - `RaffleIndexer.RaffleOptions` with `const string SectionName = "Raffle"` and properties `RpcUrl`, `ContractAddress`, `StartBlock`, `ChunkSize`, `ConfirmationBlocks`, `PollIntervalSeconds`.
  - `GET /health` → `{ lastIndexedBlock, chainHeadBlock, blocksBehind, caughtUp, updatedAt }`, sourced entirely from the `indexer_cursor` row.
  - `public partial class Program;` at the end of `Program.cs`, so `WebApplicationFactory<Program>` works in Task 8.

- [ ] **Step 1: Write the options class**

Create `src/RaffleIndexer/RaffleOptions.cs`:

```csharp
namespace RaffleIndexer;

public class RaffleOptions
{
    public const string SectionName = "Raffle";

    public string RpcUrl { get; set; } = "";
    public string ContractAddress { get; set; } = "0x1bf825d2a79f84c0c500f0797c5012d9724973d9";

    // Sepolia deploy block of the Raffle contract: backfill genesis.
    public long StartBlock { get; set; } = 11514209;

    // Tunable without a rebuild, because public RPC endpoints throttle.
    public int ChunkSize { get; set; } = 2000;

    // Index only up to latestBlock - ConfirmationBlocks. Buys reorg safety
    // without rollback machinery.
    public int ConfirmationBlocks { get; set; } = 5;

    // Sepolia block time.
    public int PollIntervalSeconds { get; set; } = 12;
}
```

- [ ] **Step 2: Wire configuration, migration, and cursor seeding into Program.cs**

Replace `src/RaffleIndexer/Program.cs` entirely:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RaffleIndexer;
using RaffleIndexer.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RaffleOptions>(
    builder.Configuration.GetSection(RaffleOptions.SectionName));

builder.Services.AddDbContext<IndexerDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
     .UseSnakeCaseNamingConvention());

var app = builder.Build();

// Single-service deployment, so the API owns migrations. This is exactly the
// decision that gets interesting in phase 2, when a second container wants them.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IndexerDbContext>();
    var options = scope.ServiceProvider.GetRequiredService<IOptions<RaffleOptions>>().Value;

    await db.Database.MigrateAsync();

    if (!await db.Cursor.AnyAsync())
    {
        db.Cursor.Add(new IndexerCursor
        {
            Id = IndexerDbContext.CursorRowId,
            // "Last block fully indexed" — one before genesis, so the first
            // chunk starts exactly at the deploy block.
            LastIndexedBlock = options.StartBlock - 1,
            ChainHeadBlock = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", async (IndexerDbContext db) =>
{
    var cursor = await db.Cursor.AsNoTracking()
        .SingleAsync(c => c.Id == IndexerDbContext.CursorRowId);

    var behind = Math.Max(0, cursor.ChainHeadBlock - cursor.LastIndexedBlock);

    return Results.Ok(new
    {
        lastIndexedBlock = cursor.LastIndexedBlock,
        chainHeadBlock = cursor.ChainHeadBlock,
        blocksBehind = behind,
        caughtUp = cursor.ChainHeadBlock > 0 && behind <= 5,
        updatedAt = cursor.UpdatedAt
    });
});

app.Run();

// Makes the implicit entry point visible to WebApplicationFactory in Task 8.
public partial class Program;
```

- [ ] **Step 3: Add the contract config to Compose**

In `docker-compose.yml`, extend the `api` service's `environment` block:

```yaml
    environment:
      ConnectionStrings__Default: "Host=db;Database=indexer;Username=indexer;Password=${POSTGRES_PASSWORD}"
      Raffle__RpcUrl: ${SEPOLIA_RPC_URL}
      Raffle__ContractAddress: "0x1bf825d2a79f84c0c500f0797c5012d9724973d9"
      Raffle__StartBlock: "11514209"
```

The `__` separator is how .NET binds a flat environment variable onto nested configuration: `Raffle__StartBlock` fills `RaffleOptions.StartBlock`.

- [ ] **Step 4: Rebuild and verify /health reports the seeded cursor**

```bash
docker compose up --build -d
curl http://localhost:8080/health
```

Expected: `{"lastIndexedBlock":11514208,"chainHeadBlock":0,"blocksBehind":0,"caughtUp":false,...}` — the deploy block minus one. If the request 500s, `docker compose logs api` will show the migration failure.

- [ ] **Step 5: Verify the schema actually landed in Postgres**

```bash
docker compose exec db psql -U indexer -d indexer -c "\dt"
docker compose exec db psql -U indexer -d indexer -c "\d rounds"
```

Expected: tables `rounds`, `entries`, `indexer_cursor`, `indexer_metadata`, `__EFMigrationsHistory`; `rounds.prize_wei` typed `numeric(78,0)`.

- [ ] **Step 6: Verify restart safety**

```bash
docker compose restart api
curl http://localhost:8080/health
docker compose exec db psql -U indexer -d indexer -c "SELECT count(*) FROM indexer_cursor;"
```

Expected: `lastIndexedBlock` unchanged at `11514208`, and exactly one cursor row — the seed did not run twice.

- [ ] **Step 7: Commit**

```bash
git add src/RaffleIndexer docker-compose.yml
git commit -m "feat: migrate on startup, seed cursor, serve /health from Postgres"
```

---

### Task 4: Chain client and log decoding

Nethereum behind a narrow interface, plus a pure decoder that turns a raw `FilterLog` into a normalized event. The decoder is where the lowercase-address constraint is enforced, and it is testable with no network.

**Files:**
- Create: `src/RaffleIndexer/Chain/RaffleEvent.cs`, `Chain/EventDtos.cs`, `Chain/RaffleLogDecoder.cs`, `Chain/IChainClient.cs`, `Chain/ChainClient.cs`
- Create: `tests/RaffleIndexer.UnitTests/RaffleIndexer.UnitTests.csproj`, `tests/RaffleIndexer.UnitTests/RaffleLogDecoderTests.cs`
- Modify: `src/RaffleIndexer/RaffleIndexer.csproj`, `src/RaffleIndexer/Program.cs`, `RaffleIndexer.sln`

**Interfaces:**
- Consumes: `RaffleOptions` from Task 3.
- Produces:
  - `RaffleIndexer.Chain.RaffleEventKind` — enum `Enter | Requested | WinnerPicked`.
  - `RaffleIndexer.Chain.RaffleEvent` — `sealed record RaffleEvent(RaffleEventKind Kind, long BlockNumber, int LogIndex, string TxHash, string? Address = null, BigInteger? RequestId = null)` with init-only `DateTimeOffset BlockTime` and `BigInteger? PrizeWei`.
  - `RaffleIndexer.Chain.RaffleLogDecoder.Decode(FilterLog log) → RaffleEvent?` (static; null for a log this contract did not emit).
  - `RaffleIndexer.Chain.IChainClient` with `Task<long> GetLatestBlockNumberAsync(CancellationToken)`, `Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(long fromBlock, long toBlock, CancellationToken)`, `Task<DateTimeOffset> GetBlockTimestampAsync(long, CancellationToken)`, `Task<BigInteger> GetBalanceAtBlockAsync(long, CancellationToken)`, `Task<BigInteger> GetEntranceFeeAsync(CancellationToken)`.

- [ ] **Step 1: Add Nethereum and pin Newtonsoft.Json**

```bash
dotnet add src/RaffleIndexer package Nethereum.Web3
dotnet add src/RaffleIndexer package Newtonsoft.Json
```

Nethereum.Web3 6.1.0 transitively pulls Newtonsoft.Json 11.0.2, which trips `NU1903` for a known high-severity advisory. The direct reference bumps it to 13.x — fixing the warning rather than suppressing it.

Run: `dotnet build`
Expected: build succeeds with no `NU1903`.

- [ ] **Step 2: Write the normalized event record**

Create `src/RaffleIndexer/Chain/RaffleEvent.cs`:

```csharp
using System.Numerics;

namespace RaffleIndexer.Chain;

public enum RaffleEventKind
{
    Enter,
    Requested,
    WinnerPicked
}

/// <summary>
/// One decoded contract event, normalized across the three event types.
/// <para>
/// The chain client fills everything except <see cref="BlockTime"/> and
/// <see cref="PrizeWei"/>; those need extra RPC calls, so the worker enriches
/// them before handing the event to the projector.
/// </para>
/// </summary>
public sealed record RaffleEvent(
    RaffleEventKind Kind,
    long BlockNumber,
    int LogIndex,
    string TxHash,
    // Player for Enter, winner for WinnerPicked, null for Requested. Lowercase.
    string? Address = null,
    BigInteger? RequestId = null)
{
    public DateTimeOffset BlockTime { get; init; }

    // Only set for WinnerPicked, by the worker.
    public BigInteger? PrizeWei { get; init; }
}
```

- [ ] **Step 3: Write the Nethereum DTOs**

Create `src/RaffleIndexer/Chain/EventDtos.cs`:

```csharp
using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace RaffleIndexer.Chain;

[Event("RaffleEnter")]
public class RaffleEnterEventDto : IEventDTO
{
    [Parameter("address", "player", 1, true)]
    public string Player { get; set; } = "";
}

[Event("RequestedRaffleWinner")]
public class RequestedRaffleWinnerEventDto : IEventDTO
{
    [Parameter("uint256", "requestId", 1, true)]
    public BigInteger RequestId { get; set; }
}

[Event("WinnerPicked")]
public class WinnerPickedEventDto : IEventDTO
{
    [Parameter("address", "winner", 1, true)]
    public string Winner { get; set; } = "";
}

[Function("getEntranceFee", "uint256")]
public class GetEntranceFeeFunction : FunctionMessage;
```

- [ ] **Step 4: Write the failing decoder tests**

```bash
dotnet new xunit -o tests/RaffleIndexer.UnitTests -f net10.0
dotnet sln add tests/RaffleIndexer.UnitTests/RaffleIndexer.UnitTests.csproj
dotnet add tests/RaffleIndexer.UnitTests reference src/RaffleIndexer/RaffleIndexer.csproj

# The template ships an empty UnitTest1.cs that asserts nothing but still counts
# as a passing test, which inflates every test-count check in this plan.
rm tests/RaffleIndexer.UnitTests/UnitTest1.cs
```

**`RaffleLogDecoder` belongs in `src/RaffleIndexer/Chain/`, not in the test project.** The tests pass either way, because the class compiles into whichever assembly holds it — but `ChainClient` in Step 9 calls `RaffleLogDecoder.Decode`, and the app cannot reference a test project. Putting it in the test project fails two steps later with a confusing "does not exist in the current context".

Create `tests/RaffleIndexer.UnitTests/RaffleLogDecoderTests.cs`. The topic hashes are the real ones from `cast keccak`, so these fixtures are byte-identical to what Sepolia returns:

```csharp
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
        // Nethereum hands back an EIP-55 checksummed address; the DB stores lowercase.
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

        Assert.Equal(
            BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935"),
            evt!.RequestId);
    }

    [Fact]
    public void Decodes_WinnerPicked_and_lowercases_the_winner_address()
    {
        var log = Log(WinnerTopic,
            "0x000000000000000000000000a4e6adae4e9b607b865aada5b9811444926ff531");

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
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/RaffleIndexer.UnitTests`
Expected: FAIL — `RaffleLogDecoder` does not exist (compile error `CS0103`).

- [ ] **Step 6: Write the decoder**

Create `src/RaffleIndexer/Chain/RaffleLogDecoder.cs`:

```csharp
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

    // Nethereum returns indexed addresses EIP-55 checksummed. Everything below
    // the API layer stores and compares lowercase.
    private static string Normalize(string address) => address.ToLowerInvariant();
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/RaffleIndexer.UnitTests`
Expected: PASS, 5 tests.

- [ ] **Step 8: Write the chain client interface**

Create `src/RaffleIndexer/Chain/IChainClient.cs`:

```csharp
using System.Numerics;

namespace RaffleIndexer.Chain;

/// <summary>The only surface the worker knows about, so the loop can be tested with a fake.</summary>
public interface IChainClient
{
    Task<long> GetLatestBlockNumberAsync(CancellationToken ct);

    /// <summary>Decoded Raffle events in [fromBlock, toBlock], ordered by (block, logIndex).</summary>
    Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(long fromBlock, long toBlock, CancellationToken ct);

    Task<DateTimeOffset> GetBlockTimestampAsync(long blockNumber, CancellationToken ct);

    /// <summary>Contract ETH balance as of the end of the given block.</summary>
    Task<BigInteger> GetBalanceAtBlockAsync(long blockNumber, CancellationToken ct);

    Task<BigInteger> GetEntranceFeeAsync(CancellationToken ct);
}
```

- [ ] **Step 9: Write the Nethereum implementation**

Create `src/RaffleIndexer/Chain/ChainClient.cs`:

```csharp
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

    public async Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(
        long fromBlock, long toBlock, CancellationToken ct)
    {
        // No topic filter: the contract emits exactly three event types, so one
        // eth_getLogs call per chunk beats three filtered ones.
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
        var block = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber
            .SendRequestAsync(new BlockParameter(new HexBigInteger(blockNumber)));
        return DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value);
    }

    public async Task<BigInteger> GetBalanceAtBlockAsync(long blockNumber, CancellationToken ct)
    {
        var balance = await _web3.Eth.GetBalance.SendRequestAsync(
            _options.ContractAddress, new BlockParameter(new HexBigInteger(blockNumber)));
        return balance.Value;
    }

    public Task<BigInteger> GetEntranceFeeAsync(CancellationToken ct) =>
        _web3.Eth.GetContractQueryHandler<GetEntranceFeeFunction>()
            .QueryAsync<BigInteger>(_options.ContractAddress, new GetEntranceFeeFunction());
}
```

- [ ] **Step 10: Register the client**

In `src/RaffleIndexer/Program.cs`, add below the `AddDbContext` registration:

```csharp
builder.Services.AddSingleton<RaffleIndexer.Chain.IChainClient, RaffleIndexer.Chain.ChainClient>();
```

- [ ] **Step 11: Verify against a real node**

Put a working Sepolia URL in `.env` as `SEPOLIA_RPC_URL`, then check the client end to end. Temporarily add to `Program.cs`, above `app.Run()`:

```csharp
app.MapGet("/debug/chain", async (RaffleIndexer.Chain.IChainClient chain, CancellationToken ct) =>
{
    var head = await chain.GetLatestBlockNumberAsync(ct);
    var fee = await chain.GetEntranceFeeAsync(ct);
    var events = await chain.GetEventsAsync(11514209, 11516209, ct);
    return Results.Ok(new
    {
        head,
        entranceFeeWei = fee.ToString(),
        eventCount = events.Count,
        first = events.Take(3).Select(e => new { kind = e.Kind.ToString(), e.BlockNumber, e.LogIndex, e.Address })
    });
});
```

```bash
docker compose up --build -d
curl http://localhost:8080/debug/chain
```

Expected: `head` is a current Sepolia block number (above 11,500,000), `entranceFeeWei` is `10000000000000000` (0.01 ETH, matching `getSepoliaEthConfig()` in the lottery repo), `eventCount` is at least 1, and any addresses come back lowercase.

- [ ] **Step 12: Remove the debug endpoint**

Delete the `/debug/chain` block from `Program.cs`. It was scaffolding for Step 11, not a deliverable — shipping it would violate the API-never-touches-the-chain invariant.

Run: `dotnet build`
Expected: succeeds, and `grep -r "debug/chain" src/` returns nothing.

- [ ] **Step 13: Commit**

```bash
git add src/RaffleIndexer tests/RaffleIndexer.UnitTests RaffleIndexer.sln
git commit -m "feat: decode Raffle events and query the chain through IChainClient"
```

---

### Task 5: The round projector

The core of the project. A pure function that turns an ordered event stream into round and entry mutations, synthesizing round structure the contract never emits. No chain, no database — the bulk of the test suite lives here.

**Files:**
- Create: `src/RaffleIndexer/Indexing/ProjectionTypes.cs`, `src/RaffleIndexer/Indexing/RaffleProjector.cs`
- Create: `tests/RaffleIndexer.UnitTests/RaffleProjectorTests.cs`

**Interfaces:**
- Consumes: `RaffleEvent`, `RaffleEventKind` from Task 4; `RoundStatus` from Task 2.
- Produces:
  - `RaffleIndexer.Indexing.RoundSnapshot(int Id, RoundStatus Status, long OpenedAtBlock, DateTimeOffset OpenedAtTime, long? RequestedAtBlock, BigInteger? RequestId, long? SettledAtBlock, DateTimeOffset? SettledAtTime, string? WinnerAddress, BigInteger? PrizeWei, int EntryCount)`.
  - `RaffleIndexer.Indexing.EntrySnapshot(int RoundId, string PlayerAddress, long BlockNumber, DateTimeOffset BlockTime, string TxHash, int LogIndex)`.
  - `RaffleIndexer.Indexing.ProjectionState(int LastRoundId, RoundSnapshot? ActiveRound)` with `static ProjectionState Empty`.
  - `RaffleIndexer.Indexing.ProjectionResult(IReadOnlyList<RoundSnapshot> TouchedRounds, IReadOnlyList<EntrySnapshot> NewEntries)`.
  - `RaffleIndexer.Indexing.RaffleProjector.Project(ProjectionState state, IReadOnlyList<RaffleEvent> events) → ProjectionResult` — static and pure.

- [ ] **Step 1: Write the projection types**

Create `src/RaffleIndexer/Indexing/ProjectionTypes.cs`:

```csharp
using System.Numerics;
using RaffleIndexer.Data;

namespace RaffleIndexer.Indexing;

/// <summary>Complete desired state of one round after a batch. The writer upserts these by Id.</summary>
public sealed record RoundSnapshot(
    int Id,
    RoundStatus Status,
    long OpenedAtBlock,
    DateTimeOffset OpenedAtTime,
    long? RequestedAtBlock,
    BigInteger? RequestId,
    long? SettledAtBlock,
    DateTimeOffset? SettledAtTime,
    string? WinnerAddress,
    BigInteger? PrizeWei,
    int EntryCount);

public sealed record EntrySnapshot(
    int RoundId,
    string PlayerAddress,
    long BlockNumber,
    DateTimeOffset BlockTime,
    string TxHash,
    int LogIndex);

/// <summary>
/// What the projector needs to know about history before this batch: the
/// highest round number ever assigned, and the round still in flight (if any).
/// Loaded from the database at batch start, which is how a round survives a
/// chunk boundary.
/// </summary>
public sealed record ProjectionState(int LastRoundId, RoundSnapshot? ActiveRound)
{
    public static ProjectionState Empty { get; } = new(0, null);
}

public sealed record ProjectionResult(
    IReadOnlyList<RoundSnapshot> TouchedRounds,
    IReadOnlyList<EntrySnapshot> NewEntries);
```

- [ ] **Step 2: Write the failing projector tests**

Create `tests/RaffleIndexer.UnitTests/RaffleProjectorTests.cs`:

```csharp
using System.Numerics;
using RaffleIndexer.Chain;
using RaffleIndexer.Data;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.UnitTests;

public class RaffleProjectorTests
{
    private const string Alice = "0xaaaa000000000000000000000000000000000001";
    private const string Bob = "0xbbbb000000000000000000000000000000000002";

    private static DateTimeOffset At(long block) =>
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + block * 12);

    private static RaffleEvent Enter(string player, long block, int logIndex = 0) =>
        new(RaffleEventKind.Enter, block, logIndex, $"0xtx{block}_{logIndex}", Address: player)
        { BlockTime = At(block) };

    private static RaffleEvent Requested(BigInteger requestId, long block, int logIndex = 0) =>
        new(RaffleEventKind.Requested, block, logIndex, $"0xtx{block}_{logIndex}", RequestId: requestId)
        { BlockTime = At(block) };

    private static RaffleEvent Won(string winner, long block, BigInteger prizeWei, int logIndex = 0) =>
        new(RaffleEventKind.WinnerPicked, block, logIndex, $"0xtx{block}_{logIndex}", Address: winner)
        { BlockTime = At(block), PrizeWei = prizeWei };

    [Fact]
    public void Opens_round_one_lazily_on_the_first_entry()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty, [Enter(Alice, 100)]);

        var round = Assert.Single(result.TouchedRounds);
        Assert.Equal(1, round.Id);
        Assert.Equal(RoundStatus.Open, round.Status);
        Assert.Equal(100, round.OpenedAtBlock);
        Assert.Equal(At(100), round.OpenedAtTime);
        Assert.Equal(1, round.EntryCount);
    }

    [Fact]
    public void An_empty_batch_produces_no_phantom_round()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty, []);

        Assert.Empty(result.TouchedRounds);
        Assert.Empty(result.NewEntries);
    }

    [Fact]
    public void Runs_a_full_round_from_entries_through_settlement()
    {
        var prize = BigInteger.Parse("30000000000000000"); // 0.03 ETH

        var result = RaffleProjector.Project(ProjectionState.Empty,
        [
            Enter(Alice, 100),
            Enter(Bob, 101),
            Enter(Alice, 102, logIndex: 1),
            Requested(new BigInteger(777), 110),
            Won(Bob, 115, prize)
        ]);

        var round = Assert.Single(result.TouchedRounds);
        Assert.Equal(1, round.Id);
        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.Equal(3, round.EntryCount);
        Assert.Equal(110, round.RequestedAtBlock);
        Assert.Equal(new BigInteger(777), round.RequestId);
        Assert.Equal(115, round.SettledAtBlock);
        Assert.Equal(At(115), round.SettledAtTime);
        Assert.Equal(Bob, round.WinnerAddress);
        Assert.Equal(prize, round.PrizeWei);

        Assert.Equal(3, result.NewEntries.Count);
        Assert.All(result.NewEntries, e => Assert.Equal(1, e.RoundId));
        Assert.Equal([Alice, Bob, Alice], result.NewEntries.Select(e => e.PlayerAddress));
    }

    [Fact]
    public void Opens_the_next_round_after_settlement()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty,
        [
            Enter(Alice, 100),
            Requested(new BigInteger(1), 110),
            Won(Alice, 115, BigInteger.One),
            Enter(Bob, 120)
        ]);

        Assert.Equal(2, result.TouchedRounds.Count);

        var first = result.TouchedRounds.Single(r => r.Id == 1);
        Assert.Equal(RoundStatus.Settled, first.Status);

        var second = result.TouchedRounds.Single(r => r.Id == 2);
        Assert.Equal(RoundStatus.Open, second.Status);
        Assert.Equal(120, second.OpenedAtBlock);
        Assert.Equal(1, second.EntryCount);
    }

    [Fact]
    public void Processes_events_in_block_and_log_index_order_regardless_of_input_order()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty,
        [
            Enter(Bob, 100, logIndex: 3),
            Enter(Alice, 100, logIndex: 1)
        ]);

        Assert.Equal([Alice, Bob], result.NewEntries.Select(e => e.PlayerAddress));
    }

    [Fact]
    public void A_round_survives_a_chunk_boundary()
    {
        // Batch 1 ends mid-round.
        var first = RaffleProjector.Project(ProjectionState.Empty,
            [Enter(Alice, 100), Enter(Bob, 101)]);

        var carried = Assert.Single(first.TouchedRounds);
        Assert.Equal(RoundStatus.Open, carried.Status);

        // Batch 2 resumes from the state the writer would have reloaded.
        var second = RaffleProjector.Project(
            new ProjectionState(LastRoundId: 1, ActiveRound: carried),
            [Requested(new BigInteger(5), 110), Won(Alice, 115, new BigInteger(20))]);

        var settled = Assert.Single(second.TouchedRounds);
        Assert.Equal(1, settled.Id);
        Assert.Equal(RoundStatus.Settled, settled.Status);
        // The entry count from batch 1 is preserved, not reset.
        Assert.Equal(2, settled.EntryCount);
        Assert.Equal(100, settled.OpenedAtBlock);
    }

    [Fact]
    public void A_round_whose_VRF_never_fulfills_stays_Calculating()
    {
        var result = RaffleProjector.Project(ProjectionState.Empty,
            [Enter(Alice, 100), Requested(new BigInteger(9), 110)]);

        var round = Assert.Single(result.TouchedRounds);
        Assert.Equal(RoundStatus.Calculating, round.Status);
        Assert.Null(round.SettledAtBlock);
        Assert.Null(round.WinnerAddress);
        Assert.Null(round.PrizeWei);
    }

    [Fact]
    public void Replaying_the_same_batch_produces_identical_output()
    {
        RaffleEvent[] batch =
        [
            Enter(Alice, 100),
            Requested(new BigInteger(1), 110),
            Won(Alice, 115, new BigInteger(7))
        ];

        var first = RaffleProjector.Project(ProjectionState.Empty, batch);
        var second = RaffleProjector.Project(ProjectionState.Empty, batch);

        Assert.Equal(first.TouchedRounds, second.TouchedRounds);
        Assert.Equal(first.NewEntries, second.NewEntries);
    }

    [Fact]
    public void Throws_when_settlement_arrives_with_no_round_in_flight()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RaffleProjector.Project(ProjectionState.Empty, [Won(Alice, 115, BigInteger.One)]));

        Assert.Contains("no round is in flight", ex.Message);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/RaffleIndexer.UnitTests --filter RaffleProjectorTests`
Expected: FAIL — `RaffleProjector` does not exist (compile error `CS0103`).

- [ ] **Step 4: Write the projector**

Create `src/RaffleIndexer/Indexing/RaffleProjector.cs`:

```csharp
using RaffleIndexer.Chain;
using RaffleIndexer.Data;

namespace RaffleIndexer.Indexing;

/// <summary>
/// Turns an ordered event stream into round and entry mutations.
/// <para>
/// The contract emits no round identifier, so round structure is synthesized
/// here: a round opens lazily on the first <c>RaffleEnter</c> after the
/// previous one settled, which is why an idle contract accumulates no phantom
/// rounds.
/// </para>
/// <para>
/// Depends on nothing — no chain, no database. That is what makes round
/// synthesis testable with hand-written event lists.
/// </para>
/// </summary>
public static class RaffleProjector
{
    public static ProjectionResult Project(ProjectionState state, IReadOnlyList<RaffleEvent> events)
    {
        var touched = new Dictionary<int, RoundSnapshot>();
        var entries = new List<EntrySnapshot>();

        var lastRoundId = state.LastRoundId;
        var active = state.ActiveRound;

        foreach (var e in events.OrderBy(x => x.BlockNumber).ThenBy(x => x.LogIndex))
        {
            switch (e.Kind)
            {
                case RaffleEventKind.Enter:
                    active ??= NewRound(++lastRoundId, e);
                    active = active with { EntryCount = active.EntryCount + 1 };
                    entries.Add(new EntrySnapshot(
                        active.Id, e.Address!, e.BlockNumber, e.BlockTime, e.TxHash, e.LogIndex));
                    touched[active.Id] = active;
                    break;

                case RaffleEventKind.Requested:
                    active = Require(active, e) with
                    {
                        Status = RoundStatus.Calculating,
                        RequestedAtBlock = e.BlockNumber,
                        RequestId = e.RequestId
                    };
                    touched[active.Id] = active;
                    break;

                case RaffleEventKind.WinnerPicked:
                    var settled = Require(active, e) with
                    {
                        Status = RoundStatus.Settled,
                        SettledAtBlock = e.BlockNumber,
                        SettledAtTime = e.BlockTime,
                        WinnerAddress = e.Address,
                        PrizeWei = e.PrizeWei
                    };
                    touched[settled.Id] = settled;
                    // The next RaffleEnter opens round N+1.
                    active = null;
                    break;
            }
        }

        return new ProjectionResult(touched.Values.OrderBy(r => r.Id).ToList(), entries);
    }

    private static RoundSnapshot NewRound(int id, RaffleEvent e) => new(
        Id: id,
        Status: RoundStatus.Open,
        OpenedAtBlock: e.BlockNumber,
        OpenedAtTime: e.BlockTime,
        RequestedAtBlock: null,
        RequestId: null,
        SettledAtBlock: null,
        SettledAtTime: null,
        WinnerAddress: null,
        PrizeWei: null,
        EntryCount: 0);

    // Not a defensive branch for a transition the contract already enforces —
    // an assertion that the caller loaded state before projecting. Reaching it
    // means the batch started mid-history, and a clear message beats a
    // NullReferenceException three layers down.
    private static RoundSnapshot Require(RoundSnapshot? active, RaffleEvent e) =>
        active ?? throw new InvalidOperationException(
            $"{e.Kind} at block {e.BlockNumber} log {e.LogIndex}: no round is in flight. " +
            "Projection state was not loaded, or indexing did not start at the deploy block.");
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/RaffleIndexer.UnitTests`
Expected: PASS, 14 tests (5 decoder + 9 projector).

- [ ] **Step 6: Commit**

```bash
git add src/RaffleIndexer/Indexing tests/RaffleIndexer.UnitTests
git commit -m "feat: synthesize round structure with a pure RaffleProjector"
```

---

### Task 6: Transactional batch commit

The persistence layer. Projection output becomes rows, and the cursor advance happens in the *same* transaction — so a container killed mid-backfill resumes exactly where it stopped instead of skipping or duplicating a chunk.

**Files:**
- Create: `src/RaffleIndexer/Data/IIndexerStore.cs`, `src/RaffleIndexer/Data/IndexerStore.cs`
- Create: `tests/RaffleIndexer.IntegrationTests/IndexerStoreTests.cs`
- Modify: `tests/RaffleIndexer.IntegrationTests/PostgresFixture.cs` (add `ResetAsync`), `src/RaffleIndexer/Program.cs`

**Interfaces:**
- Consumes: `IndexerDbContext`, entities (Task 2); `ProjectionResult`, `ProjectionState`, `RoundSnapshot`, `EntrySnapshot` (Task 5).
- Produces:
  - `RaffleIndexer.Data.IIndexerStore` with `Task<long> GetLastIndexedBlockAsync(CancellationToken)`, `Task<ProjectionState> LoadProjectionStateAsync(CancellationToken)`, `Task CommitBatchAsync(ProjectionResult result, long lastIndexedBlock, long chainHead, CancellationToken)`, `Task SetEntranceFeeAsync(BigInteger entranceFeeWei, CancellationToken)`.
  - `RaffleIndexer.Data.IndexerStore` — scoped EF implementation.
  - `PostgresFixture.ResetAsync()` — truncates tables and re-seeds the cursor at `11514208`.

- [ ] **Step 1: Add the reset helper to the fixture**

Add to `tests/RaffleIndexer.IntegrationTests/PostgresFixture.cs`, inside the `PostgresFixture` class:

```csharp
    /// <summary>Truncates all data and re-seeds the cursor. Call at the start of each test.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE entries, rounds, indexer_metadata, indexer_cursor RESTART IDENTITY CASCADE;");
        db.Cursor.Add(new IndexerCursor
        {
            Id = IndexerDbContext.CursorRowId,
            LastIndexedBlock = 11514208,
            ChainHeadBlock = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
```

`ExecuteSqlRawAsync` needs `using Microsoft.EntityFrameworkCore;`, which the file already has.

- [ ] **Step 2: Write the failing store tests**

Create `tests/RaffleIndexer.IntegrationTests/IndexerStoreTests.cs`:

```csharp
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class IndexerStoreTests(PostgresFixture fixture)
{
    private const string Alice = "0xaaaa000000000000000000000000000000000001";

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static RoundSnapshot OpenRound(int id, int entryCount) => new(
        id, RoundStatus.Open, 100, T0, null, null, null, null, null, null, entryCount);

    private static ProjectionResult Batch(RoundSnapshot round, params EntrySnapshot[] entries) =>
        new([round], entries);

    [Fact]
    public async Task Commits_rounds_entries_and_the_cursor_together()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();
        var store = new IndexerStore(db);

        await store.CommitBatchAsync(
            Batch(OpenRound(1, 1), new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0)),
            lastIndexedBlock: 11516209,
            chainHead: 11516214,
            CancellationToken.None);

        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.Rounds.CountAsync());
        Assert.Equal(1, await read.Entries.CountAsync());
        var cursor = await read.Cursor.SingleAsync();
        Assert.Equal(11516209, cursor.LastIndexedBlock);
        Assert.Equal(11516214, cursor.ChainHeadBlock);
    }

    [Fact]
    public async Task Replaying_the_same_batch_writes_no_duplicates()
    {
        await fixture.ResetAsync();
        var batch = Batch(OpenRound(1, 1), new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0));

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(batch, 11516209, 11516214, CancellationToken.None);

        // Same chunk delivered twice: a crash between commit and cursor read, or a retry.
        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(batch, 11516209, 11516214, CancellationToken.None);

        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.Rounds.CountAsync());
        Assert.Equal(1, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Entry_count_matches_the_stored_entries_even_after_a_replay()
    {
        await fixture.ResetAsync();
        var entry = new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0);

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 1), entry), 11516209, 11516214, CancellationToken.None);

        // A replay carries an already-incremented count from the projector while
        // the entry insert is deduped away. The stored count must track the rows.
        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 2), entry), 11516209, 11516214, CancellationToken.None);

        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(1, round.EntryCount);
        Assert.Equal(1, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Updates_an_existing_round_in_place_rather_than_inserting_a_second_one()
    {
        await fixture.ResetAsync();

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 1), new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0)),
                11516209, 11516214, CancellationToken.None);

        var settled = new RoundSnapshot(1, RoundStatus.Settled, 100, T0, 110, new BigInteger(42),
            115, T0.AddMinutes(5), Alice, BigInteger.Parse("30000000000000000"), 1);

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                new ProjectionResult([settled], []), 11518209, 11518214, CancellationToken.None);

        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.Equal(Alice, round.WinnerAddress);
        Assert.Equal(BigInteger.Parse("30000000000000000"), round.PrizeWei);
        Assert.Equal(new BigInteger(42), round.RequestId);
    }

    [Fact]
    public async Task The_cursor_does_not_advance_when_the_batch_write_fails()
    {
        await fixture.ResetAsync();

        // Entry points at a round that neither the batch nor the database contains:
        // the foreign key rejects it, and the cursor advance must roll back with it.
        var broken = new ProjectionResult([], [new EntrySnapshot(404, Alice, 100, T0, "0xbb", 0)]);

        await using (var db = fixture.CreateContext())
        {
            var store = new IndexerStore(db);
            await Assert.ThrowsAnyAsync<DbUpdateException>(
                () => store.CommitBatchAsync(broken, 11516209, 11516214, CancellationToken.None));
        }

        await using var read = fixture.CreateContext();
        var cursor = await read.Cursor.SingleAsync();
        Assert.Equal(11514208, cursor.LastIndexedBlock);
        Assert.Equal(0, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Loads_the_in_flight_round_so_it_survives_a_chunk_boundary()
    {
        await fixture.ResetAsync();

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                Batch(OpenRound(1, 2),
                    new EntrySnapshot(1, Alice, 100, T0, "0xaa", 0),
                    new EntrySnapshot(1, Alice, 101, T0, "0xab", 0)),
                11516209, 11516214, CancellationToken.None);

        await using var db2 = fixture.CreateContext();
        var state = await new IndexerStore(db2).LoadProjectionStateAsync(CancellationToken.None);

        Assert.Equal(1, state.LastRoundId);
        Assert.NotNull(state.ActiveRound);
        Assert.Equal(RoundStatus.Open, state.ActiveRound.Status);
        Assert.Equal(2, state.ActiveRound.EntryCount);
    }

    [Fact]
    public async Task Reports_no_active_round_once_every_round_is_settled()
    {
        await fixture.ResetAsync();
        var settled = new RoundSnapshot(1, RoundStatus.Settled, 100, T0, 110, new BigInteger(1),
            115, T0, Alice, BigInteger.One, 0);

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).CommitBatchAsync(
                new ProjectionResult([settled], []), 11516209, 11516214, CancellationToken.None);

        await using var db2 = fixture.CreateContext();
        var state = await new IndexerStore(db2).LoadProjectionStateAsync(CancellationToken.None);

        Assert.Equal(1, state.LastRoundId);
        Assert.Null(state.ActiveRound);
    }

    [Fact]
    public async Task Stores_the_entrance_fee_as_metadata()
    {
        await fixture.ResetAsync();
        var fee = BigInteger.Parse("10000000000000000");

        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).SetEntranceFeeAsync(fee, CancellationToken.None);

        // Writing it twice must not violate the primary key.
        await using (var db = fixture.CreateContext())
            await new IndexerStore(db).SetEntranceFeeAsync(fee, CancellationToken.None);

        await using var read = fixture.CreateContext();
        var row = Assert.Single(await read.Metadata.ToListAsync());
        Assert.Equal(IndexerDbContext.EntranceFeeKey, row.Key);
        Assert.Equal("10000000000000000", row.Value);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/RaffleIndexer.IntegrationTests --filter IndexerStoreTests`
Expected: FAIL — `IndexerStore` does not exist (compile error `CS0246`).

- [ ] **Step 4: Write the store interface**

Create `src/RaffleIndexer/Data/IIndexerStore.cs`:

```csharp
using System.Numerics;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.Data;

public interface IIndexerStore
{
    Task<long> GetLastIndexedBlockAsync(CancellationToken ct);

    /// <summary>Round history the projector needs before a batch: last id, and the round in flight.</summary>
    Task<ProjectionState> LoadProjectionStateAsync(CancellationToken ct);

    /// <summary>
    /// Writes rounds and entries and advances the cursor in one transaction.
    /// Either the whole chunk lands or none of it does.
    /// </summary>
    Task CommitBatchAsync(ProjectionResult result, long lastIndexedBlock, long chainHead, CancellationToken ct);

    Task SetEntranceFeeAsync(BigInteger entranceFeeWei, CancellationToken ct);
}
```

- [ ] **Step 5: Write the store**

Create `src/RaffleIndexer/Data/IndexerStore.cs`:

```csharp
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.Data;

public class IndexerStore(IndexerDbContext db) : IIndexerStore
{
    public async Task<long> GetLastIndexedBlockAsync(CancellationToken ct)
    {
        var cursor = await db.Cursor.AsNoTracking()
            .SingleAsync(c => c.Id == IndexerDbContext.CursorRowId, ct);
        return cursor.LastIndexedBlock;
    }

    public async Task<ProjectionState> LoadProjectionStateAsync(CancellationToken ct)
    {
        var lastRoundId = await db.Rounds.AsNoTracking().MaxAsync(r => (int?)r.Id, ct) ?? 0;

        var active = await db.Rounds.AsNoTracking()
            .Where(r => r.Status != RoundStatus.Settled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        return new ProjectionState(lastRoundId, active is null ? null : ToSnapshot(active));
    }

    public async Task CommitBatchAsync(
        ProjectionResult result, long lastIndexedBlock, long chainHead, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await UpsertRoundsAsync(result.TouchedRounds, ct);
        await InsertNewEntriesAsync(result.NewEntries, ct);

        var cursor = await db.Cursor.SingleAsync(c => c.Id == IndexerDbContext.CursorRowId, ct);
        cursor.LastIndexedBlock = lastIndexedBlock;
        cursor.ChainHeadBlock = chainHead;
        cursor.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        // Still inside the transaction. See the method's comment for why this
        // second pass exists.
        await RecountEntriesAsync(result.TouchedRounds, ct);

        await tx.CommitAsync(ct);
    }

    public async Task SetEntranceFeeAsync(BigInteger entranceFeeWei, CancellationToken ct)
    {
        var row = await db.Metadata.FindAsync([IndexerDbContext.EntranceFeeKey], ct);
        if (row is null)
        {
            db.Metadata.Add(new IndexerMetadata
            {
                Key = IndexerDbContext.EntranceFeeKey,
                Value = entranceFeeWei.ToString()
            });
        }
        else
        {
            row.Value = entranceFeeWei.ToString();
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertRoundsAsync(IReadOnlyList<RoundSnapshot> rounds, CancellationToken ct)
    {
        if (rounds.Count == 0) return;

        var ids = rounds.Select(r => r.Id).ToList();
        var existing = await db.Rounds.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        foreach (var snapshot in rounds)
        {
            if (!existing.TryGetValue(snapshot.Id, out var row))
            {
                row = new Round { Id = snapshot.Id };
                db.Rounds.Add(row);
            }

            row.Status = snapshot.Status;
            row.OpenedAtBlock = snapshot.OpenedAtBlock;
            row.OpenedAtTime = snapshot.OpenedAtTime;
            row.RequestedAtBlock = snapshot.RequestedAtBlock;
            row.RequestId = snapshot.RequestId;
            row.SettledAtBlock = snapshot.SettledAtBlock;
            row.SettledAtTime = snapshot.SettledAtTime;
            row.WinnerAddress = snapshot.WinnerAddress;
            row.PrizeWei = snapshot.PrizeWei;
            row.EntryCount = snapshot.EntryCount;
        }
    }

    private async Task InsertNewEntriesAsync(IReadOnlyList<EntrySnapshot> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        // (tx_hash, log_index) is the idempotency key. Filtering here turns a
        // replayed chunk into a no-op instead of a unique-violation.
        var hashes = entries.Select(e => e.TxHash).Distinct().ToList();
        var known = await db.Entries
            .Where(e => hashes.Contains(e.TxHash))
            .Select(e => new { e.TxHash, e.LogIndex })
            .ToListAsync(ct);

        var seen = known.Select(k => (k.TxHash, k.LogIndex)).ToHashSet();

        foreach (var entry in entries)
        {
            // Add returns false if this key is already present, in the database
            // or earlier in this same batch.
            if (!seen.Add((entry.TxHash, entry.LogIndex))) continue;

            db.Entries.Add(new Entry
            {
                RoundId = entry.RoundId,
                PlayerAddress = entry.PlayerAddress,
                BlockNumber = entry.BlockNumber,
                BlockTime = entry.BlockTime,
                TxHash = entry.TxHash,
                LogIndex = entry.LogIndex
            });
        }
    }

    /// <summary>
    /// Makes entry_count a projection of the entries table rather than a
    /// running total.
    /// <para>
    /// The projector increments the count per event, which is correct on a
    /// first pass. But if a chunk is ever replayed, the deduped entry inserts
    /// become no-ops while the incremented count does not — the denormalized
    /// column drifts above the real row count. Recomputing here, inside the
    /// same transaction, makes the column self-healing and the replay a true
    /// no-op.
    /// </para>
    /// </summary>
    private async Task RecountEntriesAsync(IReadOnlyList<RoundSnapshot> rounds, CancellationToken ct)
    {
        if (rounds.Count == 0) return;

        var ids = rounds.Select(r => r.Id).ToList();

        var counts = await db.Entries
            .Where(e => ids.Contains(e.RoundId))
            .GroupBy(e => e.RoundId)
            .Select(g => new { RoundId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoundId, x => x.Count, ct);

        var rows = await db.Rounds.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.EntryCount = counts.GetValueOrDefault(row.Id, 0);
        }

        await db.SaveChangesAsync(ct);
    }

    private static RoundSnapshot ToSnapshot(Round r) => new(
        r.Id, r.Status, r.OpenedAtBlock, r.OpenedAtTime, r.RequestedAtBlock, r.RequestId,
        r.SettledAtBlock, r.SettledAtTime, r.WinnerAddress, r.PrizeWei, r.EntryCount);
}
```

- [ ] **Step 6: Register the store**

In `src/RaffleIndexer/Program.cs`, add below the `IChainClient` registration:

```csharp
builder.Services.AddScoped<IIndexerStore, IndexerStore>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/RaffleIndexer.IntegrationTests`
Expected: PASS, 11 tests (3 schema + 8 store).

- [ ] **Step 8: Commit**

```bash
git add src/RaffleIndexer tests/RaffleIndexer.IntegrationTests
git commit -m "feat: commit projected rows and the cursor in one transaction"
```

---

### Task 7: The indexer worker

The polling loop: chunked backfill from the deploy block, confirmation lag, enrichment with block timestamps and prize balances, then a transactional commit. Block-range arithmetic is extracted into a pure function so the rules are tested without a chain or a database.

**Files:**
- Create: `src/RaffleIndexer/Indexing/BlockRange.cs`, `src/RaffleIndexer/Indexing/IndexerWorker.cs`
- Create: `tests/RaffleIndexer.UnitTests/BlockRangeTests.cs`
- Create: `tests/RaffleIndexer.IntegrationTests/FakeChainClient.cs`, `tests/RaffleIndexer.IntegrationTests/IndexerWorkerTests.cs`
- Modify: `src/RaffleIndexer/RaffleOptions.cs`, `src/RaffleIndexer/Program.cs`

**Interfaces:**
- Consumes: `IChainClient`, `RaffleEvent` (Task 4); `RaffleProjector`, `ProjectionState` (Task 5); `IIndexerStore` (Task 6).
- Produces:
  - `RaffleIndexer.Indexing.BlockRange.Next(long lastIndexedBlock, long chainHead, int chunkSize, int confirmationBlocks) → (long From, long To)?` — static, pure; `null` when there is nothing safe to index.
  - `RaffleIndexer.Indexing.IndexerWorker : BackgroundService` with `public async Task<bool> RunOnceAsync(IIndexerStore store, CancellationToken ct)` — one iteration, returning whether a chunk was indexed.
  - `RaffleOptions.EnableIndexer` (default `true`) — lets the API-only tests in Task 8 keep the worker switched off.

- [ ] **Step 1: Write the failing block-range tests**

Create `tests/RaffleIndexer.UnitTests/BlockRangeTests.cs`:

```csharp
using RaffleIndexer.Indexing;

namespace RaffleIndexer.UnitTests;

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

        // Safe head is 12_000_000 - 5, and the chunk stops there rather than at the head.
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
        var range = BlockRange.Next(lastIndexedBlock: 5, chainHead: 9,
            chunkSize: 2000, confirmationBlocks: 0);

        Assert.Equal(6, range!.Value.From);
        Assert.Equal(9, range.Value.To);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RaffleIndexer.UnitTests --filter BlockRangeTests`
Expected: FAIL — `BlockRange` does not exist (compile error `CS0103`).

- [ ] **Step 3: Write the block-range function**

Create `src/RaffleIndexer/Indexing/BlockRange.cs`:

```csharp
namespace RaffleIndexer.Indexing;

/// <summary>
/// Decides which block range to index next. Pure arithmetic, so the two rules
/// that matter — chunk size and confirmation lag — are testable on their own.
/// </summary>
public static class BlockRange
{
    /// <summary>
    /// The next range to index, or null when everything up to the safe head is
    /// already indexed.
    /// </summary>
    public static (long From, long To)? Next(
        long lastIndexedBlock, long chainHead, int chunkSize, int confirmationBlocks)
    {
        // Reorg mitigation without rollback machinery: stay this far behind the tip.
        var safeHead = chainHead - confirmationBlocks;
        var from = lastIndexedBlock + 1;

        if (from > safeHead) return null;

        var to = Math.Min(from + chunkSize - 1, safeHead);
        return (from, to);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/RaffleIndexer.UnitTests --filter BlockRangeTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Add the indexer toggle to options**

In `src/RaffleIndexer/RaffleOptions.cs`, add:

```csharp
    // Off in API-only tests, where no RPC endpoint exists.
    public bool EnableIndexer { get; set; } = true;
```

- [ ] **Step 6: Write the fake chain client**

Create `tests/RaffleIndexer.IntegrationTests/FakeChainClient.cs`:

```csharp
using System.Numerics;
using RaffleIndexer.Chain;

namespace RaffleIndexer.IntegrationTests;

/// <summary>Scripted chain, so the worker loop can be exercised with no RPC.</summary>
public class FakeChainClient : IChainClient
{
    public long Head { get; set; }
    public List<RaffleEvent> Events { get; } = [];

    /// <summary>Contract balance keyed by block number; missing blocks read as zero.</summary>
    public Dictionary<long, BigInteger> BalancesByBlock { get; } = [];

    public BigInteger EntranceFee { get; set; } = BigInteger.Parse("10000000000000000");

    public List<(long From, long To)> GetEventsCalls { get; } = [];
    public List<long> TimestampCalls { get; } = [];

    public Task<long> GetLatestBlockNumberAsync(CancellationToken ct) => Task.FromResult(Head);

    public Task<IReadOnlyList<RaffleEvent>> GetEventsAsync(long fromBlock, long toBlock, CancellationToken ct)
    {
        GetEventsCalls.Add((fromBlock, toBlock));
        IReadOnlyList<RaffleEvent> inRange = Events
            .Where(e => e.BlockNumber >= fromBlock && e.BlockNumber <= toBlock)
            .OrderBy(e => e.BlockNumber).ThenBy(e => e.LogIndex)
            .ToList();
        return Task.FromResult(inRange);
    }

    public Task<DateTimeOffset> GetBlockTimestampAsync(long blockNumber, CancellationToken ct)
    {
        TimestampCalls.Add(blockNumber);
        return Task.FromResult(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + blockNumber * 12));
    }

    public Task<BigInteger> GetBalanceAtBlockAsync(long blockNumber, CancellationToken ct) =>
        Task.FromResult(BalancesByBlock.TryGetValue(blockNumber, out var b) ? b : BigInteger.Zero);

    public Task<BigInteger> GetEntranceFeeAsync(CancellationToken ct) => Task.FromResult(EntranceFee);
}
```

- [ ] **Step 7: Write the failing worker tests**

Create `tests/RaffleIndexer.IntegrationTests/IndexerWorkerTests.cs`:

```csharp
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaffleIndexer;
using RaffleIndexer.Chain;
using RaffleIndexer.Data;
using RaffleIndexer.Indexing;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class IndexerWorkerTests(PostgresFixture fixture)
{
    private const string Alice = "0xaaaa000000000000000000000000000000000001";
    private const long Start = 11514209;

    private static RaffleOptions Options() => new()
    {
        RpcUrl = "http://unused",
        StartBlock = Start,
        ChunkSize = 2000,
        ConfirmationBlocks = 5,
        PollIntervalSeconds = 1
    };

    private static IndexerWorker Worker(FakeChainClient chain) =>
        new(chain,
            serviceScopeFactory: null!,   // RunOnceAsync takes the store directly
            Microsoft.Extensions.Options.Options.Create(Options()),
            NullLogger<IndexerWorker>.Instance);

    private static RaffleEvent Enter(string player, long block, int logIndex = 0) =>
        new(RaffleEventKind.Enter, block, logIndex, $"0xtx{block}_{logIndex}", Address: player);

    [Fact]
    public async Task Indexes_one_chunk_and_advances_the_cursor()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 10 };
        chain.Events.Add(Enter(Alice, Start + 1));

        await using var db = fixture.CreateContext();
        var indexed = await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        Assert.True(indexed);
        Assert.Equal((Start, Start + 5), chain.GetEventsCalls.Single());

        await using var read = fixture.CreateContext();
        var cursor = await read.Cursor.SingleAsync();
        Assert.Equal(Start + 5, cursor.LastIndexedBlock);   // head - 5 confirmations
        Assert.Equal(Start + 10, cursor.ChainHeadBlock);
        Assert.Equal(1, await read.Rounds.CountAsync());
        Assert.Equal(1, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Does_nothing_when_already_caught_up_to_the_safe_head()
    {
        await fixture.ResetAsync();
        // Safe head is Start - 1, exactly where the seeded cursor sits.
        var chain = new FakeChainClient { Head = Start + 4 };

        await using var db = fixture.CreateContext();
        var indexed = await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        Assert.False(indexed);
        Assert.Empty(chain.GetEventsCalls);
    }

    [Fact]
    public async Task Backfills_across_several_chunks_carrying_a_round_over_the_boundary()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 4005 };
        chain.Events.Add(Enter(Alice, Start + 10));          // chunk 1
        chain.Events.Add(Enter(Alice, Start + 2500, 1));     // chunk 2, same round

        await using (var db = fixture.CreateContext())
        {
            var worker = Worker(chain);
            var store = new IndexerStore(db);
            Assert.True(await worker.RunOnceAsync(store, CancellationToken.None));
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.True(await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None));
        }

        Assert.Equal([(Start, Start + 1999), (Start + 2000, Start + 3999)], chain.GetEventsCalls);

        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(1, round.Id);
        Assert.Equal(2, round.EntryCount);      // the round spanned the chunk boundary
        Assert.Equal(2, await read.Entries.CountAsync());
    }

    [Fact]
    public async Task Derives_the_prize_from_the_balance_one_block_before_settlement()
    {
        await fixture.ResetAsync();
        var prize = BigInteger.Parse("30000000000000000");
        var chain = new FakeChainClient { Head = Start + 100 };
        chain.Events.Add(Enter(Alice, Start + 1));
        chain.Events.Add(new RaffleEvent(RaffleEventKind.Requested, Start + 2, 0, "0xr", RequestId: new BigInteger(7)));
        chain.Events.Add(new RaffleEvent(RaffleEventKind.WinnerPicked, Start + 3, 0, "0xw", Address: Alice));
        // fulfillRandomWords forwards the whole balance, so the prize is the
        // balance at the end of the previous block.
        chain.BalancesByBlock[Start + 2] = prize;

        await using var db = fixture.CreateContext();
        await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        await using var read = fixture.CreateContext();
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(RoundStatus.Settled, round.Status);
        Assert.Equal(prize, round.PrizeWei);
        Assert.Equal(Alice, round.WinnerAddress);
    }

    [Fact]
    public async Task Fetches_each_blocks_timestamp_only_once_per_batch()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 100 };
        // Three entries in one block: one eth_getBlockByNumber, not three.
        chain.Events.Add(Enter(Alice, Start + 1, 0));
        chain.Events.Add(Enter(Alice, Start + 1, 1));
        chain.Events.Add(Enter(Alice, Start + 1, 2));

        await using var db = fixture.CreateContext();
        await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        Assert.Equal([Start + 1], chain.TimestampCalls);
    }

    [Fact]
    public async Task Re_running_the_same_chunk_writes_no_duplicates()
    {
        await fixture.ResetAsync();
        var chain = new FakeChainClient { Head = Start + 10 };
        chain.Events.Add(Enter(Alice, Start + 1));

        await using (var db = fixture.CreateContext())
            await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        // Simulate a crash after commit but before the cursor was read back.
        await using (var db = fixture.CreateContext())
        {
            var cursor = await db.Cursor.SingleAsync();
            cursor.LastIndexedBlock = Start - 1;
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            await Worker(chain).RunOnceAsync(new IndexerStore(db), CancellationToken.None);

        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.Entries.CountAsync());
        var round = Assert.Single(await read.Rounds.ToListAsync());
        Assert.Equal(1, round.EntryCount);
    }
}
```

- [ ] **Step 8: Run the tests to verify they fail**

Run: `dotnet test tests/RaffleIndexer.IntegrationTests --filter IndexerWorkerTests`
Expected: FAIL — `IndexerWorker` does not exist (compile error `CS0246`).

- [ ] **Step 9: Write the worker**

Create `src/RaffleIndexer/Indexing/IndexerWorker.cs`:

```csharp
using System.Numerics;
using Microsoft.Extensions.Options;
using RaffleIndexer.Chain;
using RaffleIndexer.Data;

namespace RaffleIndexer.Indexing;

/// <summary>
/// Polls the chain, projects each chunk, and commits it. Owns the cursor and
/// every decision about which blocks to read; the projector owns what the
/// events mean.
/// </summary>
public class IndexerWorker(
    IChainClient chain,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<RaffleOptions> options,
    ILogger<IndexerWorker> logger) : BackgroundService
{
    private readonly RaffleOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecordEntranceFeeAsync(stoppingToken);

        var idleDelay = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IIndexerStore>();

                var indexed = await RunOnceAsync(store, stoppingToken);

                // Backfilling: go straight to the next chunk. Caught up: wait a block.
                if (!indexed) await Task.Delay(idleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The cursor did not advance, so the next pass retries this exact
                // chunk. Rate limits and transient RPC failures self-heal.
                logger.LogError(ex, "Indexing pass failed; retrying after {Delay}", idleDelay);
                await Task.Delay(idleDelay, stoppingToken);
            }
        }
    }

    /// <summary>One pass. Returns true if a chunk was indexed, false if already caught up.</summary>
    public async Task<bool> RunOnceAsync(IIndexerStore store, CancellationToken ct)
    {
        var chainHead = await chain.GetLatestBlockNumberAsync(ct);
        var lastIndexed = await store.GetLastIndexedBlockAsync(ct);

        var range = BlockRange.Next(lastIndexed, chainHead, _options.ChunkSize, _options.ConfirmationBlocks);
        if (range is null) return false;

        var (from, to) = range.Value;

        var events = await chain.GetEventsAsync(from, to, ct);
        var enriched = await EnrichAsync(events, ct);

        var state = await store.LoadProjectionStateAsync(ct);
        var result = RaffleProjector.Project(state, enriched);

        await store.CommitBatchAsync(result, to, chainHead, ct);

        if (result.TouchedRounds.Count > 0 || result.NewEntries.Count > 0)
        {
            logger.LogInformation(
                "Indexed blocks {From}-{To}: {Rounds} round(s), {Entries} entry/entries",
                from, to, result.TouchedRounds.Count, result.NewEntries.Count);
        }

        return true;
    }

    /// <summary>
    /// Adds the two values the events do not carry: the block timestamp
    /// (eth_getLogs omits it) and the prize (WinnerPicked carries no amount).
    /// Done here rather than in the projector, which stays pure.
    /// </summary>
    private async Task<IReadOnlyList<RaffleEvent>> EnrichAsync(
        IReadOnlyList<RaffleEvent> events, CancellationToken ct)
    {
        // One eth_getBlockByNumber per unique block, not per event.
        var timestamps = new Dictionary<long, DateTimeOffset>();
        var enriched = new List<RaffleEvent>(events.Count);

        foreach (var e in events)
        {
            if (!timestamps.TryGetValue(e.BlockNumber, out var blockTime))
            {
                blockTime = await chain.GetBlockTimestampAsync(e.BlockNumber, ct);
                timestamps[e.BlockNumber] = blockTime;
            }

            BigInteger? prize = null;
            if (e.Kind == RaffleEventKind.WinnerPicked)
            {
                // fulfillRandomWords forwards the contract's entire balance, so the
                // balance at the end of the previous block is the exact prize.
                prize = await chain.GetBalanceAtBlockAsync(e.BlockNumber - 1, ct);
            }

            enriched.Add(e with { BlockTime = blockTime, PrizeWei = prize });
        }

        return enriched;
    }

    private async Task RecordEntranceFeeAsync(CancellationToken ct)
    {
        try
        {
            var fee = await chain.GetEntranceFeeAsync(ct);

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIndexerStore>();
            await store.SetEntranceFeeAsync(fee, ct);
        }
        catch (Exception ex)
        {
            // Metadata only: /stats degrades, indexing does not.
            logger.LogWarning(ex, "Could not read getEntranceFee(); continuing without it");
        }
    }
}
```

- [ ] **Step 10: Register the worker**

In `src/RaffleIndexer/Program.cs`, add below the `IIndexerStore` registration:

```csharp
if (builder.Configuration.GetValue($"{RaffleOptions.SectionName}:EnableIndexer", true))
{
    builder.Services.AddHostedService<RaffleIndexer.Indexing.IndexerWorker>();
}
```

- [ ] **Step 11: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 21 unit tests and 17 integration tests.

- [ ] **Step 12: Watch it backfill against Sepolia**

```bash
docker compose up --build -d
docker compose logs -f api
```

Expected: repeated `Indexed blocks …` lines, and `curl http://localhost:8080/health` shows `lastIndexedBlock` climbing from `11514209` with `blocksBehind` shrinking. Stop following the logs with Ctrl+C once the cursor has clearly advanced past several chunks.

- [ ] **Step 13: Verify the cursor survives a container kill**

```bash
curl -s http://localhost:8080/health
docker compose restart api
sleep 5
curl -s http://localhost:8080/health
```

Expected: the second `lastIndexedBlock` is greater than or equal to the first — indexing resumed where it stopped rather than restarting at the deploy block.

- [ ] **Step 14: Commit**

```bash
git add src/RaffleIndexer tests/
git commit -m "feat: add chunked backfill worker with confirmation lag and enrichment"
```

---

### Task 8: Read API and interactive docs

The demo surface: six endpoints served entirely from Postgres, plus a Scalar page to click through. This is where addresses become EIP-55 checksummed and uint256 values become strings so JSON consumers do not silently round them.

**Files:**
- Create: `src/RaffleIndexer/Api/AddressFormatting.cs`, `Api/Responses.cs`, `Api/RoundEndpoints.cs`, `Api/MiscEndpoints.cs`
- Create: `tests/RaffleIndexer.IntegrationTests/ApiFactory.cs`, `tests/RaffleIndexer.IntegrationTests/EndpointTests.cs`
- Modify: `src/RaffleIndexer/Program.cs`, `src/RaffleIndexer/RaffleIndexer.csproj`, `tests/RaffleIndexer.IntegrationTests/RaffleIndexer.IntegrationTests.csproj`

**Interfaces:**
- Consumes: `IndexerDbContext`, entities (Task 2).
- Produces:
  - `RaffleIndexer.Api.AddressFormatting.ToChecksum(string? lowercase) → string?`.
  - Response records in `RaffleIndexer.Api`: `RoundSummary`, `RoundDetail`, `EntryView`, `PagedResult<T>`, `PlayerView`, `StatsView`, `HealthView`.
  - `RoundEndpoints.Map(WebApplication)` and `MiscEndpoints.Map(WebApplication)`.
  - Routes: `GET /rounds`, `/rounds/current`, `/rounds/{id}`, `/players/{address}`, `/stats`, `/health`; Scalar UI at `/scalar/v1`; OpenAPI document at `/openapi/v1.json`.

- [ ] **Step 1: Add the OpenAPI and Scalar packages**

```bash
dotnet add src/RaffleIndexer package Microsoft.AspNetCore.OpenApi
dotnet add src/RaffleIndexer package Scalar.AspNetCore
dotnet add tests/RaffleIndexer.IntegrationTests package Microsoft.AspNetCore.Mvc.Testing
```

ASP.NET Core 9+ dropped Swashbuckle from the templates: `AddOpenApi()` / `MapOpenApi()` serve the document but no UI, which is what Scalar supplies.

- [ ] **Step 2: Write the address formatter**

Create `src/RaffleIndexer/Api/AddressFormatting.cs`:

```csharp
using Nethereum.Util;

namespace RaffleIndexer.Api;

/// <summary>
/// Addresses are stored lowercase so lookups never need LOWER() on a column.
/// The checksum is a presentation concern, applied here and nowhere else.
/// </summary>
public static class AddressFormatting
{
    private static readonly AddressUtil Util = new();

    public static string? ToChecksum(string? lowercaseAddress) =>
        string.IsNullOrEmpty(lowercaseAddress) ? lowercaseAddress : Util.ConvertToChecksumAddress(lowercaseAddress);
}
```

- [ ] **Step 3: Write the response records**

Create `src/RaffleIndexer/Api/Responses.cs`:

```csharp
namespace RaffleIndexer.Api;

// Wei values are strings: a uint256 does not survive JSON's double, and
// JavaScript clients would silently round anything above 2^53.
public sealed record RoundSummary(
    int Id,
    string Status,
    long OpenedAtBlock,
    DateTimeOffset OpenedAtTime,
    long? SettledAtBlock,
    DateTimeOffset? SettledAtTime,
    string? Winner,
    string? PrizeWei,
    int EntryCount);

public sealed record EntryView(
    string Player,
    long BlockNumber,
    DateTimeOffset BlockTime,
    string TxHash,
    int LogIndex);

public sealed record RoundDetail(
    int Id,
    string Status,
    long OpenedAtBlock,
    DateTimeOffset OpenedAtTime,
    long? RequestedAtBlock,
    string? RequestId,
    long? SettledAtBlock,
    DateTimeOffset? SettledAtTime,
    string? Winner,
    string? PrizeWei,
    int EntryCount,
    IReadOnlyList<EntryView> Entries);

public sealed record PagedResult<T>(int Page, int PageSize, int TotalCount, IReadOnlyList<T> Items);

public sealed record PlayerView(
    string Address,
    int RoundsPlayed,
    int EntryCount,
    int Wins,
    string TotalWonWei,
    IReadOnlyList<EntryView> Entries);

public sealed record StatsView(
    int RoundsSettled,
    int RoundsTotal,
    string TotalPaidWei,
    int UniquePlayers,
    string? BiggestPrizeWei,
    string? EntranceFeeWei);

public sealed record HealthView(
    long LastIndexedBlock,
    long ChainHeadBlock,
    long BlocksBehind,
    bool CaughtUp,
    DateTimeOffset UpdatedAt);
```

- [ ] **Step 4: Write the failing endpoint tests**

Create `tests/RaffleIndexer.IntegrationTests/ApiFactory.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace RaffleIndexer.IntegrationTests;

/// <summary>
/// Boots the real app against the Testcontainers database, with the indexer
/// switched off — these tests assert the API reads Postgres, never the chain.
/// </summary>
public class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Raffle:EnableIndexer"] = "false",
                ["Raffle:RpcUrl"] = "http://unused"
            }));

        return base.CreateHost(builder);
    }
}
```

Create `tests/RaffleIndexer.IntegrationTests/EndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using RaffleIndexer.Api;
using RaffleIndexer.Data;

namespace RaffleIndexer.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public class EndpointTests(PostgresFixture fixture)
{
    private const string AliceLower = "0xa4e6adae4e9b607b865aada5b9811444926ff531";
    private const string AliceChecksummed = "0xa4E6ADaE4e9b607b865AaDa5b9811444926ff531";
    private const string BobLower = "0xbbbb000000000000000000000000000000000002";

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly BigInteger Prize = BigInteger.Parse("30000000000000000");

    private async Task SeedAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateContext();

        db.Rounds.Add(new Round
        {
            Id = 1, Status = RoundStatus.Settled,
            OpenedAtBlock = 100, OpenedAtTime = T0,
            RequestedAtBlock = 110, RequestId = new BigInteger(42),
            SettledAtBlock = 115, SettledAtTime = T0.AddMinutes(5),
            WinnerAddress = AliceLower, PrizeWei = Prize, EntryCount = 2
        });
        db.Rounds.Add(new Round
        {
            Id = 2, Status = RoundStatus.Open,
            OpenedAtBlock = 200, OpenedAtTime = T0.AddHours(1), EntryCount = 1
        });

        db.Entries.Add(new Entry { RoundId = 1, PlayerAddress = AliceLower, BlockNumber = 100, BlockTime = T0, TxHash = "0xa1", LogIndex = 0 });
        db.Entries.Add(new Entry { RoundId = 1, PlayerAddress = BobLower, BlockNumber = 101, BlockTime = T0, TxHash = "0xa2", LogIndex = 0 });
        db.Entries.Add(new Entry { RoundId = 2, PlayerAddress = AliceLower, BlockNumber = 200, BlockTime = T0.AddHours(1), TxHash = "0xa3", LogIndex = 0 });

        db.Metadata.Add(new IndexerMetadata { Key = IndexerDbContext.EntranceFeeKey, Value = "10000000000000000" });
        await db.SaveChangesAsync();
    }

    private HttpClient Client() => new ApiFactory(fixture.ConnectionString).CreateClient();

    [Fact]
    public async Task Rounds_returns_newest_first_with_checksummed_addresses()
    {
        await SeedAsync();

        var page = await Client().GetFromJsonAsync<PagedResult<RoundSummary>>("/rounds");

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal([2, 1], page.Items.Select(r => r.Id));

        var settled = page.Items.Single(r => r.Id == 1);
        Assert.Equal("Settled", settled.Status);
        Assert.Equal(AliceChecksummed, settled.Winner);
        Assert.Equal("30000000000000000", settled.PrizeWei);
    }

    [Fact]
    public async Task Rounds_can_be_filtered_by_status()
    {
        await SeedAsync();

        var page = await Client().GetFromJsonAsync<PagedResult<RoundSummary>>("/rounds?status=Open");

        Assert.Equal(1, page!.TotalCount);
        Assert.Equal(2, page.Items.Single().Id);
    }

    [Fact]
    public async Task Rounds_pages()
    {
        await SeedAsync();

        var page = await Client().GetFromJsonAsync<PagedResult<RoundSummary>>("/rounds?page=2&pageSize=1");

        Assert.Equal(2, page!.Page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.Items.Single().Id);
    }

    [Fact]
    public async Task Round_detail_includes_entries_and_the_request_id()
    {
        await SeedAsync();

        var round = await Client().GetFromJsonAsync<RoundDetail>("/rounds/1");

        Assert.Equal("42", round!.RequestId);
        Assert.Equal(2, round.Entries.Count);
        Assert.Equal(AliceChecksummed, round.Entries[0].Player);
    }

    [Fact]
    public async Task Unknown_round_is_404()
    {
        await SeedAsync();

        var response = await Client().GetAsync("/rounds/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Current_round_is_the_one_still_in_flight()
    {
        await SeedAsync();

        var round = await Client().GetFromJsonAsync<RoundDetail>("/rounds/current");

        Assert.Equal(2, round!.Id);
        Assert.Equal("Open", round.Status);
    }

    [Fact]
    public async Task Player_lookup_is_case_insensitive_and_totals_winnings()
    {
        await SeedAsync();

        // Query with the checksummed form; storage is lowercase.
        var player = await Client().GetFromJsonAsync<PlayerView>($"/players/{AliceChecksummed}");

        Assert.Equal(AliceChecksummed, player!.Address);
        Assert.Equal(2, player.RoundsPlayed);
        Assert.Equal(2, player.EntryCount);
        Assert.Equal(1, player.Wins);
        Assert.Equal("30000000000000000", player.TotalWonWei);
    }

    [Fact]
    public async Task Stats_summarize_the_indexed_history()
    {
        await SeedAsync();

        var stats = await Client().GetFromJsonAsync<StatsView>("/stats");

        Assert.Equal(1, stats!.RoundsSettled);
        Assert.Equal(2, stats.RoundsTotal);
        Assert.Equal("30000000000000000", stats.TotalPaidWei);
        Assert.Equal(2, stats.UniquePlayers);
        Assert.Equal("30000000000000000", stats.BiggestPrizeWei);
        Assert.Equal("10000000000000000", stats.EntranceFeeWei);
    }

    [Fact]
    public async Task Stats_on_an_empty_database_return_zeroes_rather_than_failing()
    {
        await fixture.ResetAsync();

        var stats = await Client().GetFromJsonAsync<StatsView>("/stats");

        Assert.Equal(0, stats!.RoundsSettled);
        Assert.Equal("0", stats.TotalPaidWei);
        Assert.Null(stats.BiggestPrizeWei);
    }

    [Fact]
    public async Task Health_reports_the_cursor()
    {
        await fixture.ResetAsync();

        var health = await Client().GetFromJsonAsync<HealthView>("/health");

        Assert.Equal(11514208, health!.LastIndexedBlock);
        Assert.False(health.CaughtUp);
    }

    [Fact]
    public async Task The_OpenAPI_document_is_served()
    {
        await fixture.ResetAsync();

        var response = await Client().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/RaffleIndexer.IntegrationTests --filter EndpointTests`
Expected: FAIL — the response records and routes do not exist yet.

- [ ] **Step 6: Write the round endpoints**

Create `src/RaffleIndexer/Api/RoundEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RaffleIndexer.Data;

namespace RaffleIndexer.Api;

public static class RoundEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rounds", async (IndexerDbContext db, string? status, int page = 1, int pageSize = 20) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.Rounds.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<RoundStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'. Use Open, Calculating, or Settled." });

                query = query.Where(r => r.Status == parsed);
            }

            var total = await query.CountAsync();

            var rounds = await query
                .OrderByDescending(r => r.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new PagedResult<RoundSummary>(
                page, pageSize, total, rounds.Select(ToSummary).ToList()));
        })
        .WithSummary("Paged round summaries, newest first. Optional status filter.");

        // Registered before /rounds/{id} so "current" is not parsed as an id.
        app.MapGet("/rounds/current", async (IndexerDbContext db) =>
        {
            var round = await db.Rounds.AsNoTracking()
                .Include(r => r.Entries)
                .Where(r => r.Status != RoundStatus.Settled)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();

            return round is null ? Results.NotFound() : Results.Ok(ToDetail(round));
        })
        .WithSummary("The round currently Open or Calculating, if any.");

        app.MapGet("/rounds/{id:int}", async (IndexerDbContext db, int id) =>
        {
            var round = await db.Rounds.AsNoTracking()
                .Include(r => r.Entries)
                .FirstOrDefaultAsync(r => r.Id == id);

            return round is null ? Results.NotFound() : Results.Ok(ToDetail(round));
        })
        .WithSummary("One round with its entries.");
    }

    internal static RoundSummary ToSummary(Round r) => new(
        r.Id, r.Status.ToString(), r.OpenedAtBlock, r.OpenedAtTime,
        r.SettledAtBlock, r.SettledAtTime,
        AddressFormatting.ToChecksum(r.WinnerAddress),
        r.PrizeWei?.ToString(),
        r.EntryCount);

    internal static RoundDetail ToDetail(Round r) => new(
        r.Id, r.Status.ToString(), r.OpenedAtBlock, r.OpenedAtTime,
        r.RequestedAtBlock, r.RequestId?.ToString(),
        r.SettledAtBlock, r.SettledAtTime,
        AddressFormatting.ToChecksum(r.WinnerAddress),
        r.PrizeWei?.ToString(),
        r.EntryCount,
        r.Entries.OrderBy(e => e.BlockNumber).ThenBy(e => e.LogIndex).Select(ToEntryView).ToList());

    internal static EntryView ToEntryView(Entry e) => new(
        AddressFormatting.ToChecksum(e.PlayerAddress)!, e.BlockNumber, e.BlockTime, e.TxHash, e.LogIndex);
}
```

- [ ] **Step 7: Write the player, stats, and health endpoints**

Create `src/RaffleIndexer/Api/MiscEndpoints.cs`:

```csharp
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RaffleIndexer.Data;

namespace RaffleIndexer.Api;

public static class MiscEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/players/{address}", async (IndexerDbContext db, string address) =>
        {
            // Storage is lowercase, so normalizing the input is all the
            // case-insensitivity this needs — no LOWER() on the column, no lost index.
            var lower = address.ToLowerInvariant();

            var entries = await db.Entries.AsNoTracking()
                .Where(e => e.PlayerAddress == lower)
                .OrderByDescending(e => e.BlockNumber).ThenByDescending(e => e.LogIndex)
                .ToListAsync();

            var wins = await db.Rounds.AsNoTracking()
                .Where(r => r.WinnerAddress == lower)
                .Select(r => r.PrizeWei)
                .ToListAsync();

            var totalWon = wins.Aggregate(BigInteger.Zero, (sum, prize) => sum + (prize ?? BigInteger.Zero));

            return Results.Ok(new PlayerView(
                AddressFormatting.ToChecksum(lower)!,
                RoundsPlayed: entries.Select(e => e.RoundId).Distinct().Count(),
                EntryCount: entries.Count,
                Wins: wins.Count,
                TotalWonWei: totalWon.ToString(),
                Entries: entries.Select(RoundEndpoints.ToEntryView).ToList()));
        })
        .WithSummary("A player's entries, rounds played, wins, and total winnings.");

        app.MapGet("/stats", async (IndexerDbContext db) =>
        {
            var prizes = await db.Rounds.AsNoTracking()
                .Where(r => r.Status == RoundStatus.Settled && r.PrizeWei != null)
                .Select(r => r.PrizeWei!.Value)
                .ToListAsync();

            var entranceFee = await db.Metadata.AsNoTracking()
                .Where(m => m.Key == IndexerDbContext.EntranceFeeKey)
                .Select(m => m.Value)
                .FirstOrDefaultAsync();

            return Results.Ok(new StatsView(
                RoundsSettled: await db.Rounds.CountAsync(r => r.Status == RoundStatus.Settled),
                RoundsTotal: await db.Rounds.CountAsync(),
                TotalPaidWei: prizes.Aggregate(BigInteger.Zero, (sum, p) => sum + p).ToString(),
                UniquePlayers: await db.Entries.Select(e => e.PlayerAddress).Distinct().CountAsync(),
                BiggestPrizeWei: prizes.Count == 0 ? null : prizes.Max().ToString(),
                EntranceFeeWei: entranceFee));
        })
        .WithSummary("Rounds settled, total paid out, unique players, biggest prize.");

        app.MapGet("/health", async (IndexerDbContext db, IOptions<RaffleOptions> options) =>
        {
            var cursor = await db.Cursor.AsNoTracking()
                .SingleAsync(c => c.Id == IndexerDbContext.CursorRowId);

            var behind = Math.Max(0, cursor.ChainHeadBlock - cursor.LastIndexedBlock);

            // The head comes from the cursor row, written by the worker: this
            // endpoint reads Postgres like every other one.
            return Results.Ok(new HealthView(
                cursor.LastIndexedBlock,
                cursor.ChainHeadBlock,
                behind,
                CaughtUp: cursor.ChainHeadBlock > 0 && behind <= options.Value.ConfirmationBlocks,
                cursor.UpdatedAt));
        })
        .WithSummary("Indexing progress: last indexed block, chain head, blocks behind.");
    }
}
```

- [ ] **Step 8: Wire the endpoints and Scalar into Program.cs**

In `src/RaffleIndexer/Program.cs`: add `builder.Services.AddOpenApi();` alongside the other service registrations, then **delete the inline `app.MapGet("/health", …)` block** written in Task 3 and replace it with:

```csharp
app.MapOpenApi();
app.MapScalarApiReference();

RaffleIndexer.Api.RoundEndpoints.Map(app);
RaffleIndexer.Api.MiscEndpoints.Map(app);

// The demo landing spot.
app.MapGet("/", () => Results.Redirect("/scalar/v1"));
```

`MapOpenApi()` is called unconditionally rather than gated on the development environment: the interactive page in the container is the deliverable, and the data is public and read-only.

Add `using Scalar.AspNetCore;` at the top.

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 21 unit tests and 28 integration tests.

If `/openapi/v1.json` 404s, `AddOpenApi()` is missing. If the Scalar route differs from `/scalar/v1` in the installed version, note the actual route from the redirect and adjust the `/` redirect target to match.

- [ ] **Step 10: Click through the live page**

```bash
docker compose up --build -d
```

Open `http://localhost:8080/` in a browser.
Expected: the Scalar page lists all six endpoints; "Send Request" on `/rounds` returns real indexed data. Note the response time — single-digit to low-double-digit milliseconds against Postgres, versus seconds for an equivalent direct-RPC log scan. That contrast is the demo.

- [ ] **Step 11: Commit**

```bash
git add src/RaffleIndexer tests/RaffleIndexer.IntegrationTests
git commit -m "feat: serve read-only round, player, and stats endpoints with Scalar UI"
```

---

### Task 9: End-to-end verification and README

Prove the whole loop against a chain the developer controls, then against the real one, and write down how to run it.

**Files:**
- Create: `README.md`
- Modify: `docs/superpowers/specs/2026-08-20-raffle-indexer-design.md` (status line only)

**Interfaces:**
- Consumes: everything.
- Produces: a verified deployment and a README.

- [ ] **Step 1: Start Anvil and deploy the contract locally**

In a separate shell, from `d:\VSCode\foundry-full\foundry-smart-contract-lottery-cu`:

```bash
make anvil
```

In another shell, same directory:

```bash
make deploy
```

Note two things from the output: the deployed `Raffle` address and the `VRFCoordinatorV2_5Mock` address. Both are printed by the deploy script; `broadcast/DeployRaffle.s.sol/31337/run-latest.json` has them if the console scrolls past.

The lottery repo has no `EnterRaffle` script — `script/Interactions.s.sol` only covers subscription setup — so the steps below drive the contract with `cast` instead.

- [ ] **Step 2: Fire entries at the local contract**

Anvil's default first key is in the lottery repo's Makefile as `DEFAULT_ANVIL_KEY`:

```bash
export ANVIL_KEY=0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80
export RAFFLE=<raffle address from step 1>

cast send $RAFFLE "enterRaffle()" --value 0.01ether --rpc-url http://localhost:8545 --private-key $ANVIL_KEY
cast send $RAFFLE "enterRaffle()" --value 0.01ether --rpc-url http://localhost:8545 --private-key $ANVIL_KEY
```

Expected: both transactions show `status 1 (success)`.

- [ ] **Step 3: Point the indexer at Anvil and confirm the entries land**

Add an override file `docker-compose.anvil.yml`:

```yaml
services:
  api:
    environment:
      # host.docker.internal reaches the host's Anvil from inside the container.
      Raffle__RpcUrl: "http://host.docker.internal:8545"
      Raffle__ContractAddress: "${RAFFLE}"
      Raffle__StartBlock: "1"
      Raffle__ConfirmationBlocks: "0"
      Raffle__PollIntervalSeconds: "2"
```

```bash
docker compose down -v
docker compose -f docker-compose.yml -f docker-compose.anvil.yml up --build -d
sleep 15
curl -s http://localhost:8080/rounds
```

Expected: one round, `"status":"Open"`, `"entryCount":2`, and `/rounds/1` lists both entries with the Anvil sender's checksummed address.

- [ ] **Step 4: Settle the round and confirm it becomes Settled with a prize**

```bash
export VRF=<VRFCoordinatorV2_5Mock address from step 1>

# The contract's upkeep interval is 30s, so move Anvil's clock past it.
cast rpc evm_increaseTime 31 --rpc-url http://localhost:8545
cast rpc evm_mine --rpc-url http://localhost:8545

cast send $RAFFLE "performUpkeep(bytes)" 0x --rpc-url http://localhost:8545 --private-key $ANVIL_KEY
```

Read the `requestId` from the `RequestedRaffleWinner` log in that receipt, then fulfil it through the mock coordinator:

```bash
cast send $VRF "fulfillRandomWords(uint256,address)" <requestId> $RAFFLE \
  --rpc-url http://localhost:8545 --private-key $ANVIL_KEY

sleep 10
curl -s http://localhost:8080/rounds/1
curl -s http://localhost:8080/stats
```

Expected: round 1 is `"status":"Settled"` with a `winner`, a `prizeWei` of `20000000000000000` (two entries of 0.01 ETH), and a `requestId` matching the one above. `/stats` reports `roundsSettled: 1`.

This exercises every piece: decoding, round synthesis across all three states, balance-derived prizes, and the transactional commit.

- [ ] **Step 5: Confirm a mid-round restart loses nothing**

```bash
cast send $RAFFLE "enterRaffle()" --value 0.01ether --rpc-url http://localhost:8545 --private-key $ANVIL_KEY
docker compose -f docker-compose.yml -f docker-compose.anvil.yml restart api
sleep 10
curl -s http://localhost:8080/rounds/current
```

Expected: round 2 exists with `entryCount: 1` — the new entry was indexed exactly once across the restart, not duplicated and not skipped.

- [ ] **Step 6: Point back at Sepolia and backfill the real history**

Stop Anvil. Then:

```bash
docker compose down -v
docker compose up --build -d
docker compose logs -f api
```

Expected: the log shows chunked backfill from `11514209`. Poll until caught up:

```bash
curl -s http://localhost:8080/health
```

Expected: `blocksBehind` falls to single digits and `caughtUp` flips to `true`.

- [ ] **Step 7: Cross-check the totals against Etherscan**

Open `https://sepolia.etherscan.io/address/0x1bf825d2a79f84c0c500f0797c5012d9724973d9#events` and compare:

```bash
curl -s http://localhost:8080/stats
curl -s "http://localhost:8080/rounds?pageSize=100"
```

Expected: the number of `RaffleEnter` logs on Etherscan equals the sum of `entryCount` across all rounds; the number of `WinnerPicked` logs equals `roundsSettled`; each winner address matches.

If Sepolia history turns out to be sparse enough that the demo looks empty, generate more with `cast send $RAFFLE "enterRaffle()" --value 0.01ether --rpc-url $SEPOLIA_RPC_URL --private-key $PRIVATE_KEY` and wait for Chainlink Automation to settle the round.

- [ ] **Step 8: Write the README**

Create `README.md`:

````markdown
# Raffle Indexer

Indexes the [`Raffle`](https://sepolia.etherscan.io/address/0x1bf825d2a79f84c0c500f0797c5012d9724973d9)
contract's events from Sepolia into Postgres and serves them as fast, queryable
read models. The contract emits no round identifier, no prize amount, and no
timestamps — all three are synthesized or derived here.

## Run it

```bash
cp .env.example .env      # then set SEPOLIA_RPC_URL and POSTGRES_PASSWORD
docker compose up --build
```

Open <http://localhost:8080/> for the interactive API page.

Backfill starts at block 11,514,209 (the deploy block) and takes a few minutes.
Watch progress at <http://localhost:8080/health>.

## Endpoints

| Endpoint | Returns |
|---|---|
| `GET /rounds?status=&page=&pageSize=` | paged round summaries, newest first |
| `GET /rounds/{id}` | one round with its entries |
| `GET /rounds/current` | the round currently Open or Calculating |
| `GET /players/{address}` | entries, rounds played, wins, total won |
| `GET /stats` | rounds settled, total paid, unique players, biggest prize |
| `GET /health` | last indexed block, chain head, blocks behind |

Every endpoint reads Postgres. None of them touch the chain — that is the point
of an indexer, and why `/rounds` answers in milliseconds where an equivalent
direct-RPC log scan takes seconds.

## How it works

```
Sepolia RPC
    |  eth_getLogs(from, to, address)
    v
ChainClient --decoded events--> RaffleProjector --> EF Core --> Postgres
    ^                                                              |
    +--------- cursor: last_indexed_block <------------------------+
                                                                   |
                                      Minimal API <----------------+
```

- **Round synthesis.** A round opens lazily on the first `RaffleEnter`, moves to
  `Calculating` on `RequestedRaffleWinner`, and settles on `WinnerPicked`. A
  round whose VRF request never fulfils stays `Calculating` — that is a real
  on-chain state, not a bug.
- **Prize derivation.** `WinnerPicked` carries no amount, but
  `fulfillRandomWords` forwards the contract's whole balance, so the prize is
  `eth_getBalance(raffle, settledBlock - 1)`.
- **Restart safety.** Event writes and the cursor advance commit in one
  transaction, so a container killed mid-backfill resumes exactly where it
  stopped. `UNIQUE (tx_hash, log_index)` makes a replayed chunk a no-op.
- **Reorgs.** Mitigated with a 5-block confirmation lag rather than rollback
  machinery — a deliberate trade-off at this scope.
- **uint256.** Stored as `numeric(78,0)` and handled as `BigInteger`; C#
  `decimal` holds only ~29 of the 78 digits.

## Tests

```bash
dotnet test
```

Unit tests cover log decoding and round synthesis with no network or database.
Integration tests run against a real Postgres via Testcontainers, so Docker must
be running.

## Configuration

| Variable | Meaning |
|---|---|
| `SEPOLIA_RPC_URL` | RPC endpoint (`.env`, never committed) |
| `POSTGRES_PASSWORD` | database password (`.env`, never committed) |
| `Raffle__ContractAddress` | contract to index |
| `Raffle__StartBlock` | backfill genesis |
| `Raffle__ChunkSize` | blocks per backfill request — lower it if the RPC throttles |
| `Raffle__ConfirmationBlocks` | how far behind the head to stay |
| `Raffle__PollIntervalSeconds` | tail poll interval |
````

- [ ] **Step 9: Mark the spec implemented**

In `docs/superpowers/specs/2026-08-20-raffle-indexer-design.md`, change the status line to:

```markdown
**Status:** Implemented — see [docs/superpowers/plans/2026-08-22-raffle-indexer.md](../plans/2026-08-22-raffle-indexer.md)
```

- [ ] **Step 10: Full verification pass**

```bash
dotnet build
dotnet test
docker compose down -v
docker compose up --build -d
sleep 30
curl -s http://localhost:8080/health
```

Expected: build succeeds with no warnings, all tests pass, and `/health` shows the cursor advancing from `11514208` on a fresh volume.

- [ ] **Step 11: Commit**

```bash
git add README.md docker-compose.anvil.yml docs/
git commit -m "docs: add README and verify the indexer end to end"
```

---

## Phase 2 (deferred — not part of this plan)

Split `IndexerWorker` into its own Worker Service container sharing the schema through a class library. That forces the migration-ownership question two containers create, and is deliberately sequenced after Compose is comfortable.

## Self-Review

Run against the spec after the plan was written.

**Spec coverage:** every section maps to a task. Goals → Tasks 5–8; data model → Task 2; round synthesis → Task 5; derived values → Task 7 (prize from balance, timestamps cached per block, entrance fee as metadata, entry amounts deliberately not stored); indexing loop → Task 7; API surface → Task 8; Docker → Tasks 1 and 3; testing → Tasks 4–8 plus the Anvil run in Task 9; build order → the task order itself. The spec's build order step 1 ("get the container loop working before any chain code") is Task 1, unchanged.

**Placeholders:** none. Every code step carries the actual code; every verification step names the command and the expected output.

**Type consistency:** `RaffleEvent`, `RaffleEventKind`, `RoundSnapshot`, `EntrySnapshot`, `ProjectionState`, `ProjectionResult`, `IChainClient`, `IIndexerStore`, and `BlockRange.Next` are used in later tasks exactly as defined in their producing task's Interfaces block. `RoundStatus` lives in `RaffleIndexer.Data` (Task 2) and is referenced from `RaffleIndexer.Indexing` throughout.

**Known gap, deliberate:** `IndexerWorkerTests` constructs `IndexerWorker` with `serviceScopeFactory: null!` because `RunOnceAsync` takes the store as a parameter and never touches the scope factory. If that constructor argument ever becomes load-bearing inside `RunOnceAsync`, those tests will `NullReferenceException` rather than fail quietly — which is the intended signal.
