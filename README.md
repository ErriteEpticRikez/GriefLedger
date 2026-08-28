# GriefLedger

GriefLedger is a Vintage Story server-side audit ledger for player, block, entity, and container interactions. It stores all data in PostgreSQL; SQLite databases are not imported automatically.

## PostgreSQL requirements and startup

Use PostgreSQL 17 or newer. The database role must be able to create and validate the GriefLedger tables and indexes in the selected schema. On startup, GriefLedger connects, verifies the server version, creates its table and index layout in a fresh schema, and validates an existing layout before accepting game activity. It does not create a named `DB_SCHEMA`; create that schema first. It fails startup if the database is unreachable, configuration is invalid, PostgreSQL is older than 17, or an existing schema is incompatible. Restore or migrate legacy SQLite data separately before enabling the mod.

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
VINTAGE_STORY=/path/to/VintageStory dotnet test tests/GriefLedger.PostgresIntegrationTests/GriefLedger.PostgresIntegrationTests.csproj -c Release
```

The integration test uses the configured PostgreSQL instance and the configured `DB_PORT` (with process environment values taking precedence over `/opt/app/.env`), creates a temporary `gl_it_…` schema, and removes it when finished. It requires the same database settings above and PostgreSQL 17.x.

## In-game commands

All commands require the `griefledger` privilege.

- `/rollbackbreaks -p USERNAME -r #` — revert logged block breaks in a radius (default: 5).
- `/blocklog -p # -r #` — inspect a looked-at block, or an area around you with a radius.
- `/entitylog -p # -r #` or `/entitylog -p # -e ENTITYID` — inspect nearby entity activity or one entity’s history.
- `/containerlog -p #` — inspect the looked-at container’s history.
- `/tpboatid -e ENTITYID` — teleport a boat to you by entity ID.
