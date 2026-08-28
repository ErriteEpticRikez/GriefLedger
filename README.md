# GriefLedger

GriefLedger is a Vintage Story server-side audit ledger for player, block, entity, and container interactions. It stores all data in PostgreSQL; SQLite databases are not imported automatically.

## PostgreSQL requirements and startup

Use PostgreSQL 17 or newer. The database role must be able to create and validate the GriefLedger tables and indexes in the selected schema. On startup, GriefLedger connects, verifies the server version, applies additive schema migrations, and validates the resulting layout before accepting game activity. Existing `blocklogs` data remains unchanged; exact before/after block states use the separate append-only `blockmutationlogs` ledger. It does not create a named `DB_SCHEMA`; create that schema first. It fails startup if the database is unreachable, configuration is invalid, PostgreSQL is older than 17, or an existing schema is incompatible. Restore or migrate legacy SQLite data separately before enabling the mod.

Ledger constraints enforce supported domains, rollback field pairing, source ordering, and referential integrity. PostgreSQL check constraints cannot inspect another row without a trigger, so the FIFO append transaction additionally locks the source row and verifies that it is a committed Mutation at the same dimension and absolute coordinates. Direct SQL writers must preserve that cross-row rule themselves; GriefLedger exposes append-only APIs and does not issue updates or deletes.

Schema validation uses PostgreSQL 17's non-pretty canonical catalog deparse for CHECK constraints and partial-index predicates, plus structural catalog fields for keys, ordering, uniqueness, and referential actions. This is part of the PostgreSQL 17+ schema compatibility contract.

Set these required settings as process environment variables:

```text
DB_HOST=postgres.example.internal
DB_PORT=5432
DB_NAME=griefledger
DB_USER=griefledger
DB_PASSWORD=replace-with-a-secret-managed-outside-this-repository
```

`DB_SCHEMA` is optional. It must be a valid PostgreSQL identifier and isolates GriefLedger’s tables from other applications. Process environment variables take precedence. For any missing setting only, GriefLedger reads `/opt/app/.env`; this is useful for managed deployments. Do not commit that file or credentials.

The log history is intentionally retained until your database retention policy removes it. Plan disk capacity, backups, and archival/partitioning around the expected long-term write volume.

## Build and test

Point `VINTAGE_STORY` at a compatible Vintage Story installation:

```bash
VINTAGE_STORY=/path/to/VintageStory dotnet restore GriefLedger.csproj
VINTAGE_STORY=/path/to/VintageStory dotnet build GriefLedger.csproj -c Release
VINTAGE_STORY=/path/to/VintageStory dotnet test tests/GriefLedger.ReflectionTests/GriefLedger.ReflectionTests.csproj -c Release
VINTAGE_STORY=/path/to/VintageStory dotnet test tests/GriefLedger.PostgresIntegrationTests/GriefLedger.PostgresIntegrationTests.csproj -c Release
```

The reflection test verifies the exact Vintage Story 1.22.7 rollback mutation targets without starting a world. The integration test uses the configured PostgreSQL instance and the configured `DB_PORT` (with process environment values taking precedence over `/opt/app/.env`), creates temporary `gl_it_…` schemas, verifies fresh and legacy bootstrap plus ledger ordering/constraints, and removes them when finished. It requires the same database settings above and PostgreSQL 17.x.

## In-game commands

All commands require the `griefledger` privilege.

- `/rollbackbreaks -p USERNAME -r #` — revert logged block breaks in a radius (default: 5).
- `/blocklog -p # -r #` — inspect a looked-at block, or an area around you with a radius.
- `/entitylog -p # -r #` or `/entitylog -p # -e ENTITYID` — inspect nearby entity activity or one entity’s history.
- `/containerlog -p #` — inspect the looked-at container’s history.
- `/tpboatid -e ENTITYID` — teleport a boat to you by entity ID.
