# Raffle Indexer — Design

**Date:** 2026-08-20
**Status:** Approved, ready for implementation planning
**Author:** Jayden Y. (with Claude)

## Context

The `Raffle` contract (Chainlink VRF v2.5 lottery, from the foundry-smart-contract-lottery
repo) is deployed to Sepolia. Reading its history today means scanning logs over RPC on
every query, which is slow and unsuitable for a UI.

This project builds a .NET Web API that indexes the contract's events into Postgres and
serves them as fast, queryable read models, with an interactive API page for demoing.

Secondary goal, stated explicitly by the author: **learn Docker.** The containerization is
not incidental packaging; it is part of the deliverable's purpose.

### Fixed facts

| | |
|---|---|
| Contract | `0x1bf825d2a79f84c0c500f0797c5012d9724973d9` |
| Chain | Sepolia (`11155111`) |
| Deploy block | `11514209` (`0xafb161`) — backfill genesis |
| .NET SDK | 10.0.300 |

### Contract events (the complete set)

```solidity
event RaffleEnter(address indexed player);
event RequestedRaffleWinner(uint256 indexed requestId);
event WinnerPicked(address indexed winner);
```

Note what is *absent*: no round identifier, no ETH amount, no timestamp. Round structure and
prize values must be derived. This is the central design problem.

## Goals

- Backfill all contract history from the deploy block, then track the chain head.
- Synthesize round structure that the contract never emits.
- Serve round / player / stats queries from the database, never from RPC.
- Survive restarts without duplicating or losing data.
- Run under Docker Compose with Postgres, as a genuine Docker learning exercise.

## Non-goals

- Reorg rollback. Mitigated by confirmation lag instead (see below). Portfolio scope.
- Multi-contract or multi-chain indexing.
- Writes of any kind. The API is strictly read-only; it never sends transactions.
- Authentication. Public read-only data.

## Approach

**Chosen: single ASP.NET Core app (Minimal API + `BackgroundService`) + Postgres, both under
Docker Compose.**

Alternatives considered and rejected:

- *Separate Worker Service + Web API from day one.* More faithful to production indexer
  architecture, but at three event types the separation is performed rather than needed, and
  it introduces EF migration-ownership-across-two-processes — a non-Docker problem that would
  consume time budgeted for learning Docker. Deferred to phase 2.
- *Nethereum's built-in `BlockchainProcessor`.* Less plumbing, battle-tested progress
  persistence. Rejected because round synthesis sits outside the framework regardless, so it
  would hide the explainable machinery (cursor, confirmation lag, chunked backfill) while
  still leaving the hard logic hand-written.
- *SQLite.* Zero infra, but a containerized SQLite file teaches nothing about Docker
  networking, volumes, or healthchecks — the actual learning goal.

## Architecture

| Component | Responsibility | Depends on |
|---|---|---|
| `IChainClient` | Fetch logs for a block range; latest block height; balance at block | Nethereum → RPC |
| `IndexerWorker : BackgroundService` | Poll loop; owns cursor; decides block ranges | `IChainClient`, projector, db |
| `RaffleProjector` | Ordered events → round/entry mutations | **nothing** (pure) |
| `IndexerDbContext` | EF Core entities + cursor | Npgsql |
| Endpoints | Read-only Minimal API + Scalar UI | db only |

```
Sepolia RPC
    |  eth_getLogs(from, to, address, topics)
    v
ChainClient --decoded events--> RaffleProjector --> EF Core --> Postgres
    ^                                                              |
    +--------- cursor: last_indexed_block <------------------------+
                                                                   |
                                      Minimal API <----------------+
                                           +--> Scalar UI
```

### Invariants

1. **The API never touches the chain.** Endpoints read Postgres only. This is the point of an
   indexer and the core demo moment: `/rounds` returns in single-digit ms where an equivalent
   direct-RPC scan takes seconds.
2. **Confirmation lag, not reorg rollback.** Index only up to `latestBlock - 5`. Buys most
   reorg safety for a handful of lines and no rollback machinery. An honest, defensible
   trade-off at this scope.
3. **Cursor advance and event writes commit in one transaction.** A container killed
   mid-backfill resumes exactly where it stopped. Non-optional under Docker, where containers
   die routinely.
4. **`RaffleProjector` depends on nothing.** No chain, no DB. Round synthesis becomes a pure
   function testable with hand-written event lists. The bulk of the test suite lives here.

## Data model

### `rounds`

```
id                 int PK            -- assigned by the projector, NOT a DB identity column:
                                     -- round number 1, 2, 3... must be reproducible on replay
status             text              -- Open | Calculating | Settled
opened_at_block    bigint            opened_at_time     timestamptz
requested_at_block bigint?           request_id         numeric(78,0)?
settled_at_block   bigint?           settled_at_time    timestamptz?
winner_address     text?             prize_wei          numeric(78,0)?
entry_count        int               -- denormalized; keeps list queries cheap
```

### `entries`

```
id, round_id FK, player_address, block_number, block_time, tx_hash, log_index
UNIQUE (tx_hash, log_index)          -- idempotency key
```

### `indexer_cursor`

Single row: `last_indexed_block`, `updated_at`.

### Storage decisions

- **uint256 maps to `System.Numerics.BigInteger` stored as `numeric(78,0)`** via an EF value
  converter. C# `decimal` holds ~29 significant digits; uint256 needs 78. Mapping to
  `decimal` silently corrupts large values. Build this in from the first migration.
- **Addresses stored lowercase, rendered EIP-55 checksummed.** Lowercase in the DB so
  lookups match without `LOWER()` on every query; checksum applied in the API response layer.
- **`UNIQUE (tx_hash, log_index)`** plus the transactional cursor advance makes a replayed
  batch a no-op rather than a duplicate. Retrofitting dedupe later is far more painful than
  including it now.

## Round synthesis

```
                 RaffleEnter (repeats)
                        |
 [Open] --RequestedRaffleWinner--> [Calculating] --WinnerPicked--> [Settled]
    ^                                                                  |
    +------------ next RaffleEnter opens round N+1 <-------------------+
```

Events are processed strictly ordered by `(blockNumber, logIndex)`.

| Event | Effect |
|---|---|
| `RaffleEnter` | If no round is Open, open round N+1 at this block. Append entry, increment `entry_count`. |
| `RequestedRaffleWinner` | Open round becomes `Calculating`; store `request_id`, `requested_at_*`. |
| `WinnerPicked` | Calculating round becomes `Settled`; store winner, prize, `settled_at_*`. |

Rounds open **lazily** on the first `RaffleEnter`, not at deploy, so an idle contract does
not accumulate phantom rounds.

### Why no defensive branches are needed

The contract enforces the transitions the projector assumes:

- `enterRaffle` reverts with `Raffle__RaffleNotOpen` unless state is `OPEN`, so no entries can
  arrive during `CALCULATING`.
- `performUpkeep` reverts with `Raffle__UpkeepNotNeeded` unless upkeep is needed, so two
  `RequestedRaffleWinner` events cannot occur without an intervening `WinnerPicked`.

### Reachable edge cases

- **VRF never fulfills**, leaving a round stuck in `Calculating` indefinitely. This is a
  legitimate on-chain state, not a bug — surface it in the API and UI.
- **A round spans a batch boundary.** The projector loads the current Open/Calculating round
  from the DB at batch start, so state survives chunking.

## Derived values

`WinnerPicked` carries no prize and `RaffleEnter` carries no ETH amount. Both are derived,
and the derivation route materially affects RPC cost:

- **Prize:** `eth_getBalance(raffle, settledBlock - 1)`. Exact, because `fulfillRandomWords`
  forwards the contract's entire balance. Costs one RPC call per settled round; rounds are
  rare.
- **Entry amounts: not stored.** Recovering `msg.value` requires `eth_getTransactionByHash`
  per entry — N extra calls for data with no value, since the entrance fee is fixed. Read
  `getEntranceFee()` once at startup and store as metadata.
- **Block timestamps:** `eth_getLogs` returns block numbers but not timestamps, so
  `eth_getBlockByNumber` is needed per *unique* block in a batch. Cache by block number so a
  block containing five entries costs one call, not five.

The worker performs the balance and timestamp lookups and passes the values *into* the
projector, preserving the projector's purity.

## Indexing loop

- Backfill in ~2,000-block chunks from `11514209`.
- Once caught up, tail on a ~12s timer (Sepolia block time).
- Never index past `latestBlock - 5` (confirmation lag).
- Each chunk: fetch logs, enrich (timestamps, balances), project, then write rows and advance
  the cursor in one transaction.

## API surface

All read-only, served from Postgres:

| Endpoint | Returns |
|---|---|
| `GET /rounds?status=&page=&pageSize=` | paged round summaries |
| `GET /rounds/{id}` | round detail with entries |
| `GET /rounds/current` | the live Open/Calculating round |
| `GET /players/{address}` | entries, rounds played, wins, total won |
| `GET /stats` | rounds settled, total paid, unique players, biggest prize |
| `GET /health` | `last_indexed_block`, chain head, blocks behind, caught-up flag |

`/health` is load-bearing: it demonstrates liveness and is the first signal when something
breaks.

**Interactive docs:** ASP.NET Core 9+ dropped Swashbuckle from templates. `AddOpenApi()` plus
`MapOpenApi()` serve the OpenAPI document at `/openapi/v1.json` but provide **no UI**. Use
`Scalar.AspNetCore` for the interactive page.

## Docker

### `Dockerfile` (multi-stage)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore            # own layer: cached until dependencies change
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final   # runtime only, ~half the size
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "RaffleIndexer.dll"]
```

Copying `.csproj` and restoring *before* copying source means editing a `.cs` file does not
re-download every NuGet package. This layer-ordering trick is most of what understanding
Docker layers means in practice.

### `.dockerignore`

`bin/`, `obj/`, `.git`, `.env`. Omitting this copies host build artifacts into the image and
produces genuinely baffling bugs.

### `docker-compose.yml`

```yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_USER: indexer
      POSTGRES_DB: indexer
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes: [pgdata:/var/lib/postgresql/data]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U indexer"]
  api:
    build: .
    depends_on:
      db: { condition: service_healthy }
    environment:
      ConnectionStrings__Default: "Host=db;Database=indexer;Username=indexer;Password=${POSTGRES_PASSWORD}"
      Raffle__RpcUrl: ${SEPOLIA_RPC_URL}
    ports: ["8080:8080"]
volumes: { pgdata: }
```

Concepts this exercises deliberately:

- **Named volumes** — data survives `docker compose down` but not `down -v`.
- **Service DNS** — `Host=db` resolves because Compose created a network.
- **`condition: service_healthy`** — plain `depends_on` waits only for *start*, which is why
  so many apps crash on first boot against Postgres.
- **`__` config binding** — .NET maps `ConnectionStrings__Default` onto nested configuration.

### Configuration and secrets

`SEPOLIA_RPC_URL` and `POSTGRES_PASSWORD` come from a gitignored `.env` that Compose loads
automatically. Never baked into the image. A committed `.env.example` documents the shape,
mirroring the convention already used in the lottery repo.

### Migrations

Applied at startup via `Database.MigrateAsync()`. Correct for a single service, and precisely
the decision that becomes interesting in phase 2 when two containers both want to run them.

## Testing

- **`RaffleProjector` unit tests** — the core of the suite. Pure function, hand-written event
  lists: happy-path round, round spanning a batch boundary, stuck-in-`Calculating`, and a
  replayed batch proving idempotency.
- **Fake `IChainClient`** — exercise the worker loop with no RPC.
- **Testcontainers for Postgres** — real Postgres in integration tests; additional Docker
  practice as a side effect.
- **Live loop against Anvil** — run local Anvil, fire `enterRaffle` via the existing
  `forge script` in the lottery repo, point the indexer at it, assert rows appear. Faster and
  more controllable than waiting on Sepolia.

## Build order

1. Scaffold project + compose skeleton; `docker compose up`; hit `/health`. **Get the
   container loop working before any chain code.**
2. EF entities + initial migration + Testcontainers harness.
3. `IChainClient` over Nethereum; decode the three events; verify against Anvil.
4. `RaffleProjector` + unit tests. *(the real work)*
5. `IndexerWorker`: cursor, chunked backfill, confirmation lag, transactional commit.
6. Endpoints + Scalar UI.
7. Point at Sepolia; backfill from `11514209`; verify totals against Etherscan.

## Phase 2 (deferred)

Split `IndexerWorker` into its own Worker Service container, sharing the schema through a
class library. Exercises multi-container orchestration and forces the migration-ownership
decision. Deliberately sequenced *after* Compose is comfortable, so that when that question
bites, Docker is already known-good.

## Risks

- **Free hosting tiers use ephemeral disks.** A redeploy without a mounted volume wipes
  Postgres and triggers a full re-backfill from `11514209`. Resolve at deploy time with a
  persistent volume or hosted Postgres.
- **Public RPC rate limits** may throttle backfill. Chunk size and tail interval are
  configuration values so they can be tuned without a rebuild.
- **Sparse contract history.** If few rounds have settled on Sepolia, the demo looks empty;
  generating additional entries via `forge script` may be needed to make it presentable.
