# Raffle Indexer

Indexes the [`Raffle`](https://sepolia.etherscan.io/address/0x45ea858Ad50F38d6Cb1056C52C72070F93cD5F3D)
contract's events from Sepolia into Postgres and serves them as fast, queryable
read models.

The contract emits three events and nothing else:

```solidity
event RaffleEnter(address indexed player);        // older deployments: EnteredRaffle
event RequestedRaffleWinner(uint256 indexed requestId);
event WinnerPicked(address indexed winner);
```

The indexer decodes both `RaffleEnter` and `EnteredRaffle` as an entry. An
earlier deployment of this contract emitted the latter, and reading the event
set from source rather than from the deployed ABI made every entry on that
contract invisible until the mismatch was found.

No round identifier. No prize amount. No timestamps. All three are synthesized
or derived here — that is most of what this project is.

## Run it

```bash
cp .env.example .env      # then set SEPOLIA_RPC_URL and POSTGRES_PASSWORD
docker compose up --build
```

Open <http://localhost:8080/> for the interactive API page.

Backfill starts at block 11,756,391 (the deploy block) and takes seconds.
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
  round whose VRF request never fulfils stays `Calculating` — a real on-chain
  state, not a bug. `RaffleProjector` depends on nothing: no chain, no database,
  so round synthesis is testable against hand-written event lists.
- **Prize derivation.** `WinnerPicked` carries no amount, but
  `fulfillRandomWords` forwards the contract's whole balance, so the prize is
  `eth_getBalance(raffle, settledBlock - 1)`. The `- 1` is load-bearing: at the
  settling block the balance is already zero.
- **Restart safety.** Event writes and the cursor advance commit in one
  transaction, so a container killed mid-backfill resumes exactly where it
  stopped. `UNIQUE (tx_hash, log_index)` makes a replayed chunk a no-op, and
  `entry_count` is recomputed from the entries table rather than incremented, so
  a replay cannot inflate it.
- **Reorgs.** Mitigated with a 5-block confirmation lag rather than rollback
  machinery — a deliberate trade-off at this scope. `/health` reports the chain
  head from the cursor row, written by the worker, so the API stays RPC-free.
- **uint256.** Stored as `numeric(78,0)` and handled as `BigInteger`. C#
  `decimal` holds only ~29 of the 78 digits and would corrupt large values
  silently. Npgsql maps `BigInteger` to `numeric` natively, with no value
  converter.
- **Addresses.** Stored lowercase so lookups need no `LOWER()` on a column, and
  rendered EIP-55 checksummed in the response layer only.

## Tests

```bash
dotnet test
```

Unit tests cover log decoding and round synthesis with no network or database.
Integration tests run against real Postgres via Testcontainers, so Docker must
be running.

## Running against a local Anvil

Faster and more controllable than waiting on Sepolia. From the lottery repo:

```bash
anvil -m 'test test test test test test test test test test test junk' --host 0.0.0.0
```

`--host 0.0.0.0` is required. Anvil binds to `127.0.0.1` by default, and the API
container reaches the host through `host.docker.internal`, which resolves to the
host's virtual adapter rather than loopback — a loopback-only Anvil refuses the
connection while `cast` keeps working from the host.

Deploy with the `cast` sequence documented in
[the plan](docs/superpowers/plans/2026-08-22-raffle-indexer.md) (task 9), then:

```bash
docker compose -f docker-compose.yml -f docker-compose.anvil.yml up --build -d
```

`make deploy` from the lottery repo does **not** work here.
`SubscriptionAPI.createSubscription` derives the subscription id from
`blockhash(block.number - 1)`, so the id computed during `forge script`
simulation — and baked into the recorded `addConsumer` calldata — differs from
the one actually created during broadcast. The script reports success, then
reverts with `InvalidSubscription`.

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
| `Raffle__EnableIndexer` | set `false` to run the API without the worker |

## Phase 2

Split `IndexerWorker` into its own Worker Service container sharing the schema
through a class library. That forces the migration-ownership question two
containers create, and is deliberately sequenced after Compose is comfortable.
