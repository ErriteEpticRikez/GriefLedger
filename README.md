# GriefLedger

GriefLedger is a Vintage Story server-side audit ledger for player, block, entity, and container interactions. It stores all data in PostgreSQL; SQLite databases are not imported automatically.

## Acknowledgements

GriefLedger is a fork of **GriefWarden**, which provided both the inspiration and the original codebase for this mod. We gratefully acknowledge GriefWarden and its contributors for the auditing foundation on which GriefLedger is built.

## PostgreSQL requirements and startup

Runtime requires PostgreSQL 17 or newer; the automated database suite is tested on PostgreSQL 17.11. The database role must be able to create and validate the GriefLedger tables and indexes in the selected schema. On startup, GriefLedger connects, verifies the server version, applies additive schema migrations, and validates the resulting layout before accepting game activity. Existing `blocklogs` data remains unchanged; exact before/after block states use the separate append-only `blockmutationlogs` ledger. It does not create a named `DB_SCHEMA`; create that schema first. It fails startup if the database is unreachable, configuration is invalid, PostgreSQL is older than 17, or an existing schema is incompatible. Restore or migrate legacy SQLite data separately before enabling the mod.

PostgreSQL constraints enforce relational, enumerated, size, rollback-field pairing, source-ordering, and referential invariants. Envelope structure and the block-asset allowlist are validated by the codec, capture, and replay layers; malformed direct-SQL envelope data fails closed when read. PostgreSQL check constraints cannot inspect another row without a trigger, so the FIFO append transaction additionally locks the source row and verifies that it is a committed Mutation at the same dimension and absolute coordinates. Direct SQL writers must preserve that cross-row rule themselves; GriefLedger exposes append-only APIs and does not issue updates or deletes.

Schema validation uses PostgreSQL 17's non-pretty canonical catalog deparse for CHECK constraints and partial-index predicates, plus structural catalog fields for keys, ordering, uniqueness, and referential actions. The runtime version gate accepts newer PostgreSQL releases, but PostgreSQL 18+ catalog behavior has not yet been validated by this test suite.

Set these required settings as process environment variables:

```text
DB_HOST=postgres.example.internal
DB_PORT=5432
DB_NAME=griefledger
DB_USER=griefledger
DB_PASSWORD=replace-with-a-secret-managed-outside-this-repository
```

For a PostgreSQL server reached across a remote or otherwise untrusted network, enable certificate and hostname verification:

```text
DB_SSL_MODE=VerifyFull
DB_SSL_ROOT_CERTIFICATE=/etc/griefledger/postgresql-root-ca.pem
```

`DB_SSL_MODE` accepts the documented Npgsql values `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, and `VerifyFull` (case-insensitive). Its compatibility default is `Prefer`, which prefers TLS but may fall back to plaintext; do not rely on it across an untrusted network. Prefer `VerifyFull` with a trusted root certificate for remote deployments. Use `Disable` only when PostgreSQL is reached over trusted local transport. `DB_SSL_ROOT_CERTIFICATE` is an optional path passed to Npgsql as its trusted root certificate.

`DB_SCHEMA` is optional. It must be a valid PostgreSQL identifier and isolates GriefLedger’s tables from other applications. Process environment variables take precedence independently for every setting. For each missing setting, including the optional TLS keys, GriefLedger reads `/opt/app/.env`; this is useful for managed deployments. Do not commit that file, credentials, or private key material.

The log history is intentionally retained until your database retention policy removes it. Plan disk capacity, backups, and archival/partitioning around the expected long-term write volume.

## Build and test

Point `VINTAGE_STORY` at a compatible Vintage Story installation:

```bash
VINTAGE_STORY=/path/to/VintageStory dotnet restore GriefLedger.csproj
VINTAGE_STORY=/path/to/VintageStory dotnet build GriefLedger.csproj -c Release
VINTAGE_STORY=/path/to/VintageStory dotnet test tests/GriefLedger.ReflectionTests/GriefLedger.ReflectionTests.csproj -c Release
VINTAGE_STORY=/path/to/VintageStory dotnet test tests/GriefLedger.PostgresIntegrationTests/GriefLedger.PostgresIntegrationTests.csproj -c Release
```

The reflection test verifies the exact Vintage Story 1.22.7 rollback mutation targets without starting a world. The integration test uses the configured PostgreSQL instance, including `DB_SSL_MODE` and `DB_SSL_ROOT_CERTIFICATE`, creates temporary `gl_it_…` schemas, verifies fresh and legacy bootstrap plus ledger ordering/constraints, and removes them when finished. It requires the same database settings above and PostgreSQL 17.x. Process environment values take precedence over `/opt/app/.env` for every setting. In this disposable development environment, `/opt/app/.env` contains a stale `DB_PORT=3306`; local PostgreSQL 17.11 validation therefore uses the explicit process override `DB_PORT=5432`.

## Exact block rollback

Exact rollback is deliberately narrower than audit logging. When all Vintage Story 1.22.7 capture seams resolve, GriefLedger writes an append-only before/after state envelope for each supported player mutation. PostgreSQL retention is not artificially capped; set database backups, archival, partitioning, and retention according to your server's expected volume.

The replay allowlist is intentionally small:

- explicit `game:air` and plain, exact `Block` instances with no block entity, fluid, or decor;
- vanilla chisel/microblock state captured from the supported vanilla microblock block entity, with its material asset codes; and
- only player block breaks, placements, vanilla chisel conversion, and vanilla chisel voxel mutations.

Chiseled blocks are restored from their captured microblock tree through the vanilla block-entity history restore path. The snapshot rejects external sub-decors, `decorIds`/`decorRot`, beams, unknown tree fields, missing assets, and malformed material mappings instead of guessing.

The following are audit-only and never replayed by `/rollbackbreaks` or `/rollbackblocks`: containers and their inventories, arbitrary block entities, fluids, decorations, entities and entity inventories, explosions, pickups, unsupported mod blocks, arbitrary modded chisel state, and all legacy `blocklogs` rows created before exact envelopes existed. Those records remain useful to inspect with the existing log commands, but cannot be converted into an exact rollback safely.

Replay uses immutable player UIDs, not mutable display names. It takes two durable ledger cutoffs, uses bounded candidate/history reads (radius at most 256, at most 10,000 selected mutations and coordinates, at most 200,000 history rows, and independently at most 64 MiB of encoded envelopes per candidate or history read), verifies every inverse state chain newest-first, checks the current world state and capture generation on the server main thread, and records every success, skip, or failure as another append-only ledger entry. Each stored `envelope_data` value is also constrained by PostgreSQL to the codec's 2,101,248-byte maximum, including direct SQL writes. A concurrent mutation, later activity at the same coordinate, another player's modification, a missing asset, or an unexpected current state causes a safe skip/failure rather than a blind overwrite. If a safety-critical restore or audit condition stops a batch, the result is explicitly `batch-stopped` and gives the unprocessed selected count; it is never presented as complete. Only one exact replay operation runs at a time.

Exact capture is capability-gated. If the known Vintage Story seams are unavailable, GriefLedger leaves the database and legacy audit commands running but disables exact rollback with a clear command error. Captures made before the exact envelope ledger are inspection-only.

## In-game commands

All commands require the `griefledger` privilege.

The exact rollback commands center their guarded radius on the operator's authoritative current world position and dimension. They therefore require an in-world player and reject the server console instead of inventing coordinates. The inspection commands keep their existing behavior.

- `/rollbackbreaks -u PLAYERUID -r # [-b BEFORE_SOURCE_ID]` — exactly revert supported, captured block breaks in a radius (default: 5; maximum: 256). This is the recommended form.
- `/rollbackblocks -u PLAYERUID -r # [-b BEFORE_SOURCE_ID]` — exactly revert supported, captured breaks, placements, and vanilla chisel mutations in a radius (default: 5; maximum: 256).
- `/rollbackbreaks -p USERNAME -r #` and `/rollbackblocks -p USERNAME -r #` — name convenience forms. They run only when the case-insensitive last-known name maps to one immutable UID; ambiguous, missing, and legacy UID-less names are rejected. Prefer `-u` for incident response.
- `/blocklog -p # -r #` — inspect a looked-at block, or an area around you with a radius.
- `/entitylog -p # -r #` or `/entitylog -p # -e ENTITYID` — inspect nearby entity activity or one entity’s history.
- `/containerlog -p #` — inspect the looked-at container’s history.
- `/tpboatid -e ENTITYID` — teleport a boat to you by entity ID.

For example, from the affected dimension and area:

```text
/rollbackbreaks -u 5d6f… -r 12
/rollbackblocks -u 5d6f… -r 12
/rollbackblocks -u 5d6f… -r 12 -b 4815162342
```

The command returns its immutable ledger cutoff and last history ID, selected/processed/unprocessed totals, succeeded/failed/skipped totals, and stable reason counts. A request processes at most 10,000 newest eligible sources and 64 MiB of candidate envelope data. When older eligible sources remain, the command labels the page incomplete and prints a positive continuation source ID; rerun the same command with that value as `-b BEFORE_SOURCE_ID`. API callers receive the same cursor as `ContinuationBeforeSourceId` and pass it back as `BeforeSourceIdExclusive`. Same-coordinate later failures still block unsafe older unwinds, and successful sources are excluded from later pages. History reads are restricted to the oldest selected source through the second durable cutoff, omit non-mutating skips and resolved mutation/inverse pairs, and retain only the latest unresolved failure per source; the remaining read is capped at 200,000 rows and 64 MiB. If history exceeds either cap, replay halves the newest-first candidate subset under the same coordinate watch and second cutoff until it fits or reaches one source, so a large page cannot permanently prevent bounded progress. The FIFO writer queue retains at most 64 work items; gameplay-only logs are dropped with a rate-limited health error when full, while awaited exact-ledger writes fail immediately. It starts asynchronously so database reads never block the Vintage Story main thread; final status is sent back on that thread. Only a final page with no failures or skips is reported as a successful completion; failed or skipped sources produce an unresolved notification. A safety stop is reported as `batch-stopped`, never as complete; an operational durability failure likewise returns an explicitly labelled partial result and stops replay.

## Live-world verification boundary

`tests/LIVE_WORLD_CAPTURE_MATRIX.md` records the manual Vintage Story world actions still required to verify captures and replay against a running server/client. The automated suite validates the real 1.22.7 reflection targets, state codecs, planner, lifecycle, and PostgreSQL ledger, but it does not simulate a connected live world. Before production use, perform that matrix on a staging server—especially a chiseled block with no external decor or beams—and confirm the command reports the expected durable outcome entries.
