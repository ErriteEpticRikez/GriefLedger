using System.Buffers.Binary;
using System.Text;
using GriefLedger;
using GriefLedger.Rollback;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace GriefLedger.PostgresIntegrationTests;

public sealed class Postgres17IntegrationTests {
    private static readonly string[] RequiredSettings = ["DB_HOST", "DB_PORT", "DB_NAME", "DB_USER", "DB_PASSWORD"];

    [Fact]
    public void Block_state_envelope_is_deterministic_bounded_and_immutable() {
        byte[] sourceTree = [1, 2, 3, 4];
        var envelope = new BlockStateEnvelope(
            EnvelopeBlockState.Asset("game:chiseledblock", sourceTree),
            EnvelopeBlockState.Air()
        );
        byte[] encoded = envelope.Encode();
        sourceTree[0] = 99;
        byte[] exposedTree = envelope.Before.BlockEntityTreeAttributeBytes!;
        exposedTree[1] = 99;

        BlockStateEnvelope decoded = BlockStateEnvelope.Decode(encoded);
        Assert.Equal(envelope, decoded);
        Assert.Equal([1, 2, 3, 4], decoded.Before.BlockEntityTreeAttributeBytes);
        Assert.Equal(encoded, decoded.Encode());

        byte[] unknownVersion = (byte[])encoded.Clone();
        unknownVersion[5] = 2;
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(unknownVersion));
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(encoded[..^1]));
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(encoded.Concat(new byte[] { 0 }).ToArray()));
        byte[] wrongCount = (byte[])encoded.Clone();
        wrongCount[7] = 3;
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(wrongCount));
        byte[] unknownKind = (byte[])encoded.Clone();
        unknownKind[8] = 9;
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(unknownKind));
        byte[] unknownFlags = (byte[])encoded.Clone();
        unknownFlags[9] = 0x80;
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(unknownFlags));
        byte[] nonzeroReserved = (byte[])encoded.Clone();
        nonzeroReserved[10] = 1;
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(nonzeroReserved));
        byte[] oversizedAsset = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(oversizedAsset.AsSpan(12, 4), 1025);
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(oversizedAsset));
        byte[] oversizedTree = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(oversizedTree.AsSpan(16, 4), 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(oversizedTree));
        byte[] invalidUtf8 = (byte[])encoded.Clone();
        invalidUtf8[20] = 0xff;
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(invalidUtf8));
        Assert.Throws<InvalidDataException>(() => BlockStateEnvelope.Decode(new byte[BlockStateEnvelope.MaximumEncodedBytes + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnvelopeBlockState.Asset("game:" + new string('x', 1024)));

        var append = MutationAppend(envelope, 1);
        byte[] exposedEnvelope = append.EnvelopeData;
        exposedEnvelope[0] = 0;
        Assert.Equal(encoded, append.EnvelopeData);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockMutationAppend(-1, null, null,
            BlockMutationEntryKind.Mutation, BlockMutationActionKind.Break, 0, 0, 0, 0, envelope));
        Assert.Throws<ArgumentException>(() => new BlockMutationAppend(1, null, null,
            BlockMutationEntryKind.Mutation, BlockMutationActionKind.Rollback, 0, 0, 0, 0, envelope));
        Assert.Throws<ArgumentException>(() => new BlockMutationAppend(1, null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback, 0, 0, 0, 0, envelope,
            1, BlockMutationRollbackOutcome.Succeeded, operatorPlayerUid: " "));
        Assert.Throws<ArgumentException>(() => new BlockMutationAppend(1, null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback, 0, 0, 0, 0, envelope,
            1, BlockMutationRollbackOutcome.Succeeded, "unexpected", operatorPlayerUid: "operator"));
        Assert.Throws<ArgumentException>(() => new BlockMutationAppend(1, null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback, 0, 0, 0, 0, envelope,
            1, BlockMutationRollbackOutcome.Failed, operatorPlayerUid: "operator"));
        Assert.Throws<ArgumentException>(() => new BlockMutationAppend(1, null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback, 0, 0, 0, 0, envelope,
            1, BlockMutationRollbackOutcome.Failed, "Unstable message", operatorPlayerUid: "operator"));
    }

    [Fact]
    public async Task Production_database_paths_work_on_postgresql_17() {
        Dictionary<string, string?> originalSettings = CaptureProcessSettings();
        NpgsqlDataSource? admin = null;
        string? schema = null;
        bool schemaCreated = false;
        try {
            Dictionary<string, string> settings = LoadSettings();
            schema = "gl_it_" + Guid.NewGuid().ToString("N");
            SetProcessSettings(settings, schema);
            admin = CreateAdminDataSource(settings);
            await CreateSchema(admin, schema);
            schemaCreated = true;

            await AssertPostgres17(admin);
            await AssertLegacyBootstrapAndIncompatibleDetection(admin, settings, schema);
            await AssertSchemaConfigurationPrecedence(schema);
            BootstrapRepeatedly();
            await AssertBootstrapSchema(admin, schema);

            await RestartIdentitiesAboveIntMax(admin, schema);
            await AddFailureConstraint(admin, schema);
            await ExerciseConcurrentPlayerUpsert();
            ExerciseWritesCompressionFailureAndDrain();
            ExerciseFinalPlayerNameUpdate();
            await ExerciseLedgerWriterAndCutoff(admin, schema);

            using (var database = new Database()) {
                Assert.Equal((606, 70, -606), database.GetLastEntityCoordsLog("page-target"));
                Assert.Null(database.GetLastEntityCoordsLog("missing-entity"));
            }

            await AssertStoredWrites(admin, schema);
            await AssertProductionReadQueries(admin, schema);
            await AssertCompositeIndexesAreUsable(admin, schema);
        }
        finally {
            try {
                if (admin != null && schemaCreated && schema != null) await DropSchema(admin, schema);
            }
            finally {
                try {
                    if (admin != null) await admin.DisposeAsync();
                }
                finally {
                    RestoreProcessSettings(originalSettings);
                }
            }
        }
    }

    private static Dictionary<string, string?> CaptureProcessSettings() {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string key in RequiredSettings.Append("DB_SCHEMA")) values[key] = Environment.GetEnvironmentVariable(key);
        return values;
    }

    private static void RestoreProcessSettings(Dictionary<string, string?> settings) {
        foreach (var setting in settings) Environment.SetEnvironmentVariable(setting.Key, setting.Value);
    }

    private static Dictionary<string, string> LoadSettings() {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string key in RequiredSettings) values[key] = Environment.GetEnvironmentVariable(key);

        const string dotEnvPath = "/opt/app/.env";
        if (File.Exists(dotEnvPath)) {
            foreach (string rawLine in File.ReadLines(dotEnvPath)) {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line[..separator].Trim();
                if (!values.ContainsKey(key) || values[key] is not null) continue;
                string value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
                values[key] = value;
            }
        }

        var requiredValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in RequiredSettings) {
            if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)) {
                throw new InvalidOperationException("A required database setting is unavailable.");
            }
            requiredValues[key] = value;
        }
        return requiredValues;
    }

    private static void SetProcessSettings(Dictionary<string, string> settings, string schema) {
        foreach (string key in RequiredSettings) Environment.SetEnvironmentVariable(key, settings[key]);
        Environment.SetEnvironmentVariable("DB_SCHEMA", schema);
    }

    private static NpgsqlDataSource CreateAdminDataSource(Dictionary<string, string> settings) {
        var builder = new NpgsqlConnectionStringBuilder {
            Host = settings["DB_HOST"],
            Port = int.Parse(settings["DB_PORT"]),
            Database = settings["DB_NAME"],
            Username = settings["DB_USER"],
            Password = settings["DB_PASSWORD"]
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static async Task CreateSchema(NpgsqlDataSource admin, string schema) {
        await using NpgsqlCommand command = admin.CreateCommand("CREATE SCHEMA " + QuoteIdentifier(schema));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchema(NpgsqlDataSource admin, string schema) {
        if (!schema.StartsWith("gl_it_", StringComparison.Ordinal) || schema.Length != 38) {
            throw new InvalidOperationException("Refusing to clean up an unexpected schema name.");
        }
        await using NpgsqlCommand command = admin.CreateCommand("DROP SCHEMA IF EXISTS " + QuoteIdentifier(schema) + " CASCADE");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertLegacyBootstrapAndIncompatibleDetection(NpgsqlDataSource admin, Dictionary<string, string> settings, string mainSchema) {
        string legacySchema = "gl_it_" + Guid.NewGuid().ToString("N");
        string incompatibleSchema = "gl_it_" + Guid.NewGuid().ToString("N");
        string shapeSchema = "gl_it_" + Guid.NewGuid().ToString("N");
        try {
            await CreateSchema(admin, legacySchema);
            await CreateLegacyFourTables(admin, legacySchema);
            await using (NpgsqlConnection connection = await admin.OpenConnectionAsync()) {
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = $"SET search_path TO {QuoteIdentifier(legacySchema)}; INSERT INTO blocklogs (timestamp_utc, actiontype, block, itemstack_encoding, x, y, z) VALUES (1, 0, 'legacy:kept', 0, 1, 2, 3);";
                await command.ExecuteNonQueryAsync();
            }
            SetProcessSettings(settings, legacySchema);
            BootstrapRepeatedly();
            await using (NpgsqlConnection connection = await admin.OpenConnectionAsync()) {
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = $"SET search_path TO {QuoteIdentifier(legacySchema)}; SELECT to_regclass('blockmutationlogs') IS NOT NULL, (SELECT count(*) FROM blocklogs WHERE block = 'legacy:kept')";
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }

            await CreateSchema(admin, incompatibleSchema);
            await CreateLegacyFourTables(admin, incompatibleSchema);
            await using (NpgsqlConnection connection = await admin.OpenConnectionAsync()) {
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = $"SET search_path TO {QuoteIdentifier(incompatibleSchema)}; CREATE TABLE blockmutationlogs (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, timestamp_utc TEXT);";
                await command.ExecuteNonQueryAsync();
            }
            SetProcessSettings(settings, incompatibleSchema);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new Database());
            Assert.Contains("blockmutationlogs.timestamp_utc must use PostgreSQL type bigint", error.Message, StringComparison.Ordinal);

            await CreateSchema(admin, shapeSchema);
            SetProcessSettings(settings, shapeSchema);
            using (var database = new Database()) { }
            await ExecuteInSchema(admin, shapeSchema, "ALTER TABLE blockmutationlogs DROP CONSTRAINT ck_blockmutationlogs_entry_kind");
            error = Assert.Throws<InvalidOperationException>(() => new Database());
            Assert.Contains("required check constraint ck_blockmutationlogs_entry_kind was not found", error.Message, StringComparison.Ordinal);

            await DropSchema(admin, shapeSchema);
            await CreateSchema(admin, shapeSchema);
            using (var database = new Database()) { }
            await ExecuteInSchema(admin, shapeSchema, @"ALTER TABLE blockmutationlogs
                DROP CONSTRAINT ck_blockmutationlogs_failure_code_format,
                ADD CONSTRAINT ck_blockmutationlogs_failure_code_format
                CHECK (failure_code IS NULL OR failure_code ~ '^[a-z 0-9][a-z0-9._-]{0,127}$')");
            error = Assert.Throws<InvalidOperationException>(() => new Database());
            Assert.Contains("check constraint ck_blockmutationlogs_failure_code_format has an incompatible definition", error.Message, StringComparison.Ordinal);

            await DropSchema(admin, shapeSchema);
            await CreateSchema(admin, shapeSchema);
            using (var database = new Database()) { }
            await ExecuteInSchema(admin, shapeSchema, "ALTER TABLE blockmutationlogs ALTER COLUMN player_id SET NOT NULL");
            error = Assert.Throws<InvalidOperationException>(() => new Database());
            Assert.Contains("blockmutationlogs.player_id has incompatible nullability", error.Message, StringComparison.Ordinal);

            await DropSchema(admin, shapeSchema);
            await CreateSchema(admin, shapeSchema);
            using (var database = new Database()) { }
            await ExecuteInSchema(admin, shapeSchema, "DROP INDEX idx_blockmutationlogs_player_dimension_id_desc; CREATE INDEX idx_blockmutationlogs_player_dimension_id_desc ON blockmutationlogs(player_id, id DESC)");
            error = Assert.Throws<InvalidOperationException>(() => new Database());
            Assert.Contains("index idx_blockmutationlogs_player_dimension_id_desc has an incompatible definition", error.Message, StringComparison.Ordinal);
        }
        finally {
            SetProcessSettings(settings, mainSchema);
            await DropSchema(admin, legacySchema);
            await DropSchema(admin, incompatibleSchema);
            await DropSchema(admin, shapeSchema);
        }
    }

    private static async Task ExecuteInSchema(NpgsqlDataSource admin, string schema, string sql) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; " + sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLegacyFourTables(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $@"SET search_path TO {QuoteIdentifier(schema)};
            CREATE TABLE players (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, playeruid TEXT UNIQUE, last_playername TEXT);
            CREATE TABLE blocklogs (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, timestamp_utc BIGINT, player_id BIGINT NULL, actiontype INTEGER, block TEXT, itemstack_data BYTEA NULL, itemstack_encoding INTEGER NOT NULL DEFAULT 0, x INTEGER, y INTEGER, z INTEGER, oldblockid INTEGER);
            CREATE TABLE entitylogs (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, timestamp_utc BIGINT, player_id BIGINT NULL, actiontype INTEGER, entityname TEXT, entityid TEXT, itemstack_data BYTEA NULL, itemstack_encoding INTEGER NOT NULL DEFAULT 0, x INTEGER, y INTEGER, z INTEGER);
            CREATE TABLE containerlogs (id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, timestamp_utc BIGINT, player_id BIGINT NULL, containerid TEXT, itemstack_data BYTEA NULL, itemstack_encoding INTEGER NOT NULL DEFAULT 0, quantity INTEGER, actiontype INTEGER);";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static async Task AssertPostgres17(NpgsqlDataSource admin) {
        await using NpgsqlCommand command = admin.CreateCommand("SHOW server_version_num");
        int version = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.InRange(version, 170000, 179999);
    }

    private static async Task AssertSchemaConfigurationPrecedence(string schema) {
        string dotEnvPath = Path.GetTempFileName();
        try {
            await File.WriteAllTextAsync(dotEnvPath, "DB_SCHEMA=schema_that_must_not_win\n");
            Environment.SetEnvironmentVariable("DB_SCHEMA", schema);
            await using (NpgsqlDataSource environmentSource = DatabaseConfiguration.CreateDataSource(dotEnvPath)) {
                await AssertCurrentSchema(environmentSource, schema);
            }

            await File.WriteAllTextAsync(dotEnvPath, "DB_SCHEMA=" + schema + "\n");
            Environment.SetEnvironmentVariable("DB_SCHEMA", null);
            await using (NpgsqlDataSource dotEnvSource = DatabaseConfiguration.CreateDataSource(dotEnvPath)) {
                await AssertCurrentSchema(dotEnvSource, schema);
            }
        }
        finally {
            Environment.SetEnvironmentVariable("DB_SCHEMA", schema);
            File.Delete(dotEnvPath);
        }
    }

    private static async Task AssertCurrentSchema(NpgsqlDataSource dataSource, string expected) {
        await using NpgsqlCommand command = dataSource.CreateCommand("SELECT current_schema()");
        Assert.Equal(expected, Convert.ToString(await command.ExecuteScalarAsync()));
    }

    private static void BootstrapRepeatedly() {
        using (var first = new Database()) { }
        using (var second = new Database()) { }
    }

    private static async Task AssertBootstrapSchema(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand columns = connection.CreateCommand();
        columns.CommandText = "SELECT table_name, column_name, data_type, is_identity, is_nullable FROM information_schema.columns WHERE table_schema = @schema ORDER BY table_name, ordinal_position";
        columns.Parameters.AddWithValue("schema", schema);
        var rows = new List<(string Table, string Column, string Type, string Identity, string Nullable)>();
        await using (NpgsqlDataReader reader = await columns.ExecuteReaderAsync()) {
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        Assert.Equal(5, rows.Select(row => row.Table).Distinct().Count());
        Assert.All(rows.Where(row => row.Column == "id"), row => {
            Assert.Equal("bigint", row.Type);
            Assert.Equal("YES", row.Identity);
        });
        Assert.Contains(rows, row => row.Column == "itemstack_data" && row.Type == "bytea");
        string[] requiredLedgerColumns = ["id", "timestamp_utc", "entry_kind", "action_kind", "dimension", "x", "y", "z", "envelope_data", "envelope_encoding"];
        string[] optionalLedgerColumns = ["player_id", "source_mutation_id", "rollback_outcome", "failure_code", "operator_player_id"];
        Assert.All(requiredLedgerColumns, column => Assert.Contains(rows, row => row.Table == "blockmutationlogs" && row.Column == column && row.Nullable == "NO"));
        Assert.All(optionalLedgerColumns, column => Assert.Contains(rows, row => row.Table == "blockmutationlogs" && row.Column == column && row.Nullable == "YES"));

        await using NpgsqlCommand constraints = connection.CreateCommand();
        constraints.CommandText = @"SELECT constraint_row.conname, constraint_row.contype::text,
                constraint_row.confupdtype::text, constraint_row.confdeltype::text,
                constraint_row.convalidated, constraint_row.condeferrable,
                pg_get_constraintdef(constraint_row.oid, false)
            FROM pg_constraint constraint_row
            JOIN pg_class table_row ON table_row.oid = constraint_row.conrelid
            JOIN pg_namespace schema_row ON schema_row.oid = table_row.relnamespace
            WHERE schema_row.nspname = @schema AND table_row.relname = 'blockmutationlogs'";
        constraints.Parameters.AddWithValue("schema", schema);
        var constraintRows = new Dictionary<string, (string Type, string Update, string Delete, bool Validated, bool Deferrable, string Definition)>(StringComparer.Ordinal);
        await using (NpgsqlDataReader reader = await constraints.ExecuteReaderAsync()) {
            while (await reader.ReadAsync()) constraintRows[reader.GetString(0)] = (
                reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetString(6));
        }
        foreach (string foreignKey in new[] { "fk_blockmutationlogs_player", "fk_blockmutationlogs_operator", "fk_blockmutationlogs_source" }) {
            Assert.True(constraintRows.TryGetValue(foreignKey, out var constraint));
            Assert.Equal("f", constraint.Type);
            Assert.Equal("a", constraint.Update);
            Assert.Equal("r", constraint.Delete);
            Assert.True(constraint.Validated);
            Assert.False(constraint.Deferrable);
        }
        string[] checks = [
            "ck_blockmutationlogs_timestamp", "ck_blockmutationlogs_entry_kind", "ck_blockmutationlogs_action_kind",
            "ck_blockmutationlogs_envelope_encoding", "ck_blockmutationlogs_rollback_outcome",
            "ck_blockmutationlogs_failure_code_length", "ck_blockmutationlogs_failure_code_format", "ck_blockmutationlogs_entry_action_pair",
            "ck_blockmutationlogs_rollback_fields", "ck_blockmutationlogs_outcome_failure_pair",
            "ck_blockmutationlogs_source_precedes"
        ];
        Assert.All(checks, check => {
            Assert.True(constraintRows.TryGetValue(check, out var constraint));
            Assert.Equal("c", constraint.Type);
            Assert.True(constraint.Validated);
            Assert.False(constraint.Deferrable);
            Assert.StartsWith("CHECK (", constraint.Definition, StringComparison.Ordinal);
        });

        await using NpgsqlCommand indexes = connection.CreateCommand();
        indexes.CommandText = "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = @schema";
        indexes.Parameters.AddWithValue("schema", schema);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (NpgsqlDataReader reader = await indexes.ExecuteReaderAsync()) while (await reader.ReadAsync()) names[reader.GetString(0)] = reader.GetString(1);
        string[] expected = [
            "idx_blocklogs_coords", "idx_entitylogs_coords", "idx_containerlogs_id",
            "idx_entitylogs_entityid_id_desc", "idx_containerlogs_containerid_id_desc",
            "idx_blocklogs_ts", "idx_blocklogs_pid_ts", "idx_blocklogs_act_ts",
            "idx_entitylogs_ts", "idx_entitylogs_pid_ts", "idx_entitylogs_act_ts",
            "idx_containerlogs_ts", "idx_containerlogs_pid_ts", "idx_containerlogs_act_ts",
            "idx_blockmutationlogs_player_dimension_id_desc",
            "idx_blockmutationlogs_dimension_coords_id_desc",
            "idx_blockmutationlogs_source_outcome", "ux_blockmutationlogs_successful_source"
        ];
        Assert.All(expected, name => Assert.True(names.ContainsKey(name)));
        Assert.Contains("(player_id, dimension, id DESC)", names["idx_blockmutationlogs_player_dimension_id_desc"], StringComparison.Ordinal);
        Assert.Contains("(dimension, x, y, z, id DESC)", names["idx_blockmutationlogs_dimension_coords_id_desc"], StringComparison.Ordinal);
        Assert.Contains("(source_mutation_id, rollback_outcome)", names["idx_blockmutationlogs_source_outcome"], StringComparison.Ordinal);
        Assert.StartsWith("CREATE UNIQUE INDEX", names["ux_blockmutationlogs_successful_source"], StringComparison.Ordinal);
        Assert.Contains("WHERE ((entry_kind = 1) AND (rollback_outcome = 1))", names["ux_blockmutationlogs_successful_source"], StringComparison.Ordinal);
    }

    private static async Task RestartIdentitiesAboveIntMax(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; ALTER TABLE players ALTER COLUMN id RESTART WITH 2147483650; ALTER TABLE blockmutationlogs ALTER COLUMN id RESTART WITH 2147483650;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExerciseLedgerWriterAndCutoff(NpgsqlDataSource admin, string schema) {
        var envelope = new BlockStateEnvelope(EnvelopeBlockState.Asset("game:stone"), EnvelopeBlockState.Air());
        using var database = new Database();
        Task<long> first = database.EnqueueBlockMutationAppend(MutationAppend(envelope, 100));
        Task<long> second = database.EnqueueBlockMutationAppend(MutationAppend(envelope, 101));
        Task<long> cutoffTask = database.GetDurableBlockMutationCutoffAsync();
        long cutoff = await cutoffTask;
        long firstId = await first;
        long secondId = await second;
        Assert.True(firstId > int.MaxValue);
        Assert.Equal(secondId, cutoff);

        await using (NpgsqlConnection connection = await admin.OpenConnectionAsync()) {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; SELECT count(*) FROM blockmutationlogs WHERE id <= @cutoff";
            command.Parameters.AddWithValue("cutoff", cutoff);
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.Parameters.Clear();
            command.CommandText = "SELECT envelope_data, envelope_encoding, dimension, x, y, z FROM blockmutationlogs WHERE id = @id";
            command.Parameters.AddWithValue("id", firstId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(envelope, BlockStateEnvelope.Decode(reader.GetFieldValue<byte[]>(0)));
            Assert.Equal(BlockStateEnvelope.BinaryEncoding, reader.GetInt32(1));
            Assert.Equal((2, 100, 80, -100), (reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
        }

        await using (NpgsqlConnection directConnection = await admin.OpenConnectionAsync()) {
            await using (NpgsqlCommand searchPath = directConnection.CreateCommand()) {
                searchPath.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}";
                await searchPath.ExecuteNonQueryAsync();
            }
            await using NpgsqlTransaction directTransaction = await directConnection.BeginTransactionAsync();
            long uncommittedLowerId;
            await using (NpgsqlCommand directInsert = DirectMutationCommand(directConnection, directTransaction, envelope, 103)) {
                uncommittedLowerId = Convert.ToInt64(await directInsert.ExecuteScalarAsync());
            }

            Task<long> higherWriterAppend = database.EnqueueBlockMutationAppend(MutationAppend(envelope, 104));
            Task<long> racedCutoff = database.GetDurableMutationCutoffAsync();
            long higherWriterId = await higherWriterAppend;
            Assert.True(uncommittedLowerId < higherWriterId);
            Assert.NotSame(racedCutoff, await Task.WhenAny(racedCutoff, Task.Delay(250)));

            await directTransaction.CommitAsync();
            Assert.Equal(higherWriterId, await racedCutoff);

            long laterDirectId;
            await using (NpgsqlCommand laterInsert = DirectMutationCommand(directConnection, null, envelope, 105)) {
                laterDirectId = Convert.ToInt64(await laterInsert.ExecuteScalarAsync());
            }
            Assert.True(laterDirectId > higherWriterId);
            await using NpgsqlCommand prefixCheck = directConnection.CreateCommand();
            prefixCheck.CommandText = "SELECT count(*) FROM blockmutationlogs WHERE id IN (@lower, @higher) AND id <= @cutoff";
            prefixCheck.Parameters.AddWithValue("lower", uncommittedLowerId);
            prefixCheck.Parameters.AddWithValue("higher", higherWriterId);
            prefixCheck.Parameters.AddWithValue("cutoff", higherWriterId);
            Assert.Equal(2L, Convert.ToInt64(await prefixCheck.ExecuteScalarAsync()));
        }

        var rollback = new BlockMutationAppend(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback,
            2, 100, 80, -100, envelope, firstId, BlockMutationRollbackOutcome.Succeeded,
            operatorPlayerName: "Operator", operatorPlayerUid: "operator-uid"
        );
        long rollbackId = await database.EnqueueBlockMutationAppend(rollback);
        Assert.Equal(rollbackId, await database.GetDurableMutationCutoffAsync());

        var duplicateRollback = new BlockMutationAppend(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback,
            2, 100, 80, -100, envelope, firstId, BlockMutationRollbackOutcome.Succeeded,
            operatorPlayerName: "Failed Operator", operatorPlayerUid: "operator-failed-atomic"
        );
        Task<long> duplicate = database.EnqueueBlockMutationAppend(duplicateRollback);
        Task<long> firstFailedBarrier = database.GetDurableMutationCutoffAsync();
        Task<long> secondFailedBarrier = database.GetDurableMutationCutoffAsync();
        await Assert.ThrowsAsync<PostgresException>(async () => await duplicate);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstFailedBarrier);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await secondFailedBarrier);

        var rollbackOfRollback = new BlockMutationAppend(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback,
            2, 100, 80, -100, envelope, rollbackId, BlockMutationRollbackOutcome.Failed,
            "source-not-mutation", operatorPlayerUid: "operator-source-check"
        );
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await database.EnqueueBlockMutationAppend(rollbackOfRollback));
        var coordinateMismatch = new BlockMutationAppend(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback,
            2, 999, 80, -100, envelope, firstId, BlockMutationRollbackOutcome.Skipped,
            "coordinate-mismatch", operatorPlayerUid: "operator-coordinate-check"
        );
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await database.EnqueueBlockMutationAppend(coordinateMismatch));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await database.GetDurableMutationCutoffAsync());

        await using (NpgsqlConnection connection = await admin.OpenConnectionAsync()) {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $@"SET search_path TO {QuoteIdentifier(schema)};
                SELECT count(*) FROM blockmutationlogs WHERE source_mutation_id = @source AND rollback_outcome = 1;
                SELECT mutation_actor.playeruid, rollback_operator.playeruid, rollback_row.player_id
                FROM blockmutationlogs mutation_row
                JOIN players mutation_actor ON mutation_actor.id = mutation_row.player_id
                JOIN blockmutationlogs rollback_row ON rollback_row.source_mutation_id = mutation_row.id AND rollback_row.rollback_outcome = 1
                JOIN players rollback_operator ON rollback_operator.id = rollback_row.operator_player_id
                WHERE mutation_row.id = @source;
                SELECT count(*) FROM players WHERE playeruid = 'operator-failed-atomic';";
            command.Parameters.AddWithValue("source", firstId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ledger-actor", reader.GetString(0));
            Assert.Equal("operator-uid", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
        }

        var shutdownDatabase = new Database();
        Task<long> drainedAppend = shutdownDatabase.EnqueueBlockMutationAppend(MutationAppend(envelope, 102));
        shutdownDatabase.Dispose();
        Assert.True(await drainedAppend > rollbackId);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await shutdownDatabase.GetDurableMutationCutoffAsync());
    }

    private static BlockMutationAppend MutationAppend(BlockStateEnvelope envelope, int x) => new(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Ledger Actor", "ledger-actor",
        BlockMutationEntryKind.Mutation, BlockMutationActionKind.Break,
        2, x, 80, -x, envelope
    );

    private static NpgsqlCommand DirectMutationCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        BlockStateEnvelope envelope, int x) {
        NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO blockmutationlogs
            (timestamp_utc, entry_kind, action_kind, dimension, x, y, z, envelope_data, envelope_encoding)
            VALUES (@timestamp, 0, 1, 2, @x, 80, @z, @envelope, 1)
            RETURNING id;";
        command.Parameters.AddWithValue("timestamp", NpgsqlDbType.Bigint, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("x", NpgsqlDbType.Integer, x);
        command.Parameters.AddWithValue("z", NpgsqlDbType.Integer, -x);
        command.Parameters.AddWithValue("envelope", NpgsqlDbType.Bytea, envelope.Encode());
        return command;
    }

    private static async Task AddFailureConstraint(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; ALTER TABLE containerlogs ADD CONSTRAINT integration_quantity_nonnegative CHECK (quantity >= 0);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExerciseConcurrentPlayerUpsert() {
        Database[] databases = Enumerable.Range(0, 4).Select(_ => new Database()).ToArray();
        try {
            var start = new ManualResetEventSlim(false);
            Task[] submissions = databases.Select((database, index) => Task.Run(() => {
                start.Wait();
                database.AddBlockLog("Concurrent " + index, "uid-concurrent", "PLACED", "game:block/concurrent", "c" + index, index, 1, 2, null);
            })).ToArray();
            start.Set();
            await Task.WhenAll(submissions);
        }
        finally {
            await Task.WhenAll(databases.Select(database => Task.Run(database.Dispose)));
        }
    }

    private static void ExerciseWritesCompressionFailureAndDrain() {
        using var database = new Database();
        var raw = database.CompressText("x");
        Assert.Equal(Encoding.UTF8.GetBytes("x"), raw.data);
        Assert.Equal(1, raw.encoding);
        string longText = string.Concat(Enumerable.Repeat("compressible-itemstack-", 200));
        var compressed = database.CompressText(longText);
        Assert.Equal(2, compressed.encoding);
        Assert.Equal(longText, database.DecompressText(compressed.data, compressed.encoding));
        Assert.Null(database.DecompressText(null, 0));

        database.AddBlockLog(null, null, "BROKE", "game:block/null", null, 10, 20, 30, null);
        database.AddBlockLog("Raw", "uid-raw", "PLACED", "game:block/raw", "x", 11, 21, 31, 42);
        database.AddBlockLog("Rollback", "uid-rollback", "BROKE", "game:block/101", null, 50, 60, 70, null);
        database.AddBlockLog("Rollback", "uid-rollback", "PLACED", "game:block/102", null, 50, 60, 70, null);
        database.AddBlockLog("Rollback", "uid-rollback", "BROKE", "game:block/103", null, 500, 600, 700, null);
        database.AddBlockLog("Other", "uid-other", "BROKE", "game:block/104", null, 50, 60, 70, null);

        string[] actions = ["BROKE", "PLACED", "USED", "INTERACTED", "KILLED", "TAKEN", "SWAP", "SAME_ITEM", "SPAWNED", "DESPAWNED"];
        for (int index = 0; index < actions.Length; index++) {
            database.AddEntityLog(index == 0 ? null : "Actor", index == 0 ? null : "uid-actions", actions[index], "entity-action", "action-" + index, index == 1 ? longText : null, index, index + 1, index + 2);
        }
        for (int index = 1; index <= 6; index++) {
            database.AddEntityLog("Pager", "uid-pager", "INTERACTED", "page-entity", "page-target", index == 1 ? "x" : null, 600 + index, 70, -600 - index);
        }

        database.AddContainerLog(null, null, "TAKEN", "container-null", null, 0);
        database.AddContainerLog("Container", "uid-container", "TAKEN", "container-a", "x", 1);
        database.AddContainerLog("Container", "uid-container", "SWAP", "container-b", longText, 2);

        database.AddContainerLog("Invalid", "uid-invalid-write", "TAKEN", "container-invalid", "rejected", -1);
        database.AddContainerLog("AfterFailure", "uid-after-failure", "SAME_ITEM", "container-after-failure", "survived", 3);

        for (int index = 0; index < 120; index++) {
            database.AddBlockLog("Drain", "uid-drain", "USED", "game:block/drain", null, index, 90, -index, null);
        }
    }

    private static void ExerciseFinalPlayerNameUpdate() {
        using var database = new Database();
        database.AddEntityLog("Final Concurrent Name", "uid-concurrent", "USED", "update", "uid-update", null, 1, 2, 3);
    }

    private static async Task AssertStoredWrites(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; SELECT id, last_playername FROM players WHERE playeruid = 'uid-concurrent'";
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync()) {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetInt64(0) > int.MaxValue);
            Assert.Equal("Final Concurrent Name", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT count(*) FROM blocklogs WHERE block = 'game:block/drain'";
        Assert.Equal(120L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT count(*) FROM containerlogs WHERE containerid = 'container-after-failure'";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT count(*) FROM containerlogs WHERE containerid = 'container-invalid'";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT count(*) FROM blocklogs WHERE block = 'game:block/concurrent'";
        Assert.Equal(4L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = @"SELECT count(*)
            FROM blocklogs block_row
            JOIN players player_row ON player_row.id = block_row.player_id
            WHERE block_row.block = 'game:block/concurrent'
              AND player_row.playeruid = 'uid-concurrent'";
        Assert.Equal(4L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT player_id, itemstack_data, itemstack_encoding, oldblockid FROM blocklogs WHERE block = 'game:block/null'";
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync()) {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.True(reader.IsDBNull(3));
        }
        command.CommandText = "SELECT itemstack_data, itemstack_encoding, oldblockid FROM blocklogs WHERE block = 'game:block/raw'";
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync()) {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(Encoding.UTF8.GetBytes("x"), reader.GetFieldValue<byte[]>(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(42, reader.GetInt32(2));
        }
        command.CommandText = "SELECT actiontype FROM entitylogs WHERE entityname = 'entity-action' ORDER BY entityid";
        var actionValues = new List<int>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) actionValues.Add(reader.GetInt32(0));
        Assert.Equal(Enumerable.Range(0, 10), actionValues);

        command.CommandText = "SELECT itemstack_data, itemstack_encoding FROM containerlogs WHERE containerid = 'container-b'";
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync()) {
            Assert.True(await reader.ReadAsync());
            byte[] data = reader.GetFieldValue<byte[]>(0);
            Assert.Equal(2, reader.GetInt32(1));
            using var database = new Database();
            Assert.Equal(string.Concat(Enumerable.Repeat("compressible-itemstack-", 200)), database.DecompressText(data, 2));
        }
    }

    private static async Task AssertProductionReadQueries(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand setup = connection.CreateCommand();
        setup.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}";
        await setup.ExecuteNonQueryAsync();

        List<long> pageIds = await ReadIds(connection, Database.EntityLogByEntityIdQuery, command => {
            Add(command, "entityid", NpgsqlDbType.Text, "page-target");
            Add(command, "loglimit", NpgsqlDbType.Integer, 4);
            Add(command, "skiplognum", NpgsqlDbType.Integer, 0);
        });
        Assert.Equal(4, pageIds.Count);
        Assert.Equal(pageIds.Order(), pageIds);
        await using (NpgsqlCommand newest = connection.CreateCommand()) {
            newest.CommandText = "SELECT id FROM entitylogs WHERE entityid = 'page-target' ORDER BY id DESC LIMIT 4";
            var expected = new List<long>();
            await using NpgsqlDataReader reader = await newest.ExecuteReaderAsync();
            while (await reader.ReadAsync()) expected.Add(reader.GetInt64(0));
            expected.Sort();
            Assert.Equal(expected, pageIds);
        }

        Assert.Single(await ReadIds(connection, Database.ContainerLogQuery, command => {
            Add(command, "containerids", NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { "container-a" });
            Add(command, "loglimit", NpgsqlDbType.Integer, 20);
            Add(command, "skiplognum", NpgsqlDbType.Integer, 0);
        }));
        Assert.Equal(2, (await ReadIds(connection, Database.ContainerLogQuery, command => {
            Add(command, "containerids", NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { "container-a", "container-b" });
            Add(command, "loglimit", NpgsqlDbType.Integer, 20);
            Add(command, "skiplognum", NpgsqlDbType.Integer, 0);
        })).Count);
        Assert.Empty(await ReadIds(connection, Database.ContainerLogQuery, command => {
            Add(command, "containerids", NpgsqlDbType.Array | NpgsqlDbType.Text, Array.Empty<string>());
            Add(command, "loglimit", NpgsqlDbType.Integer, 20);
            Add(command, "skiplognum", NpgsqlDbType.Integer, 0);
        }));

        long rollbackPlayerId;
        await using (NpgsqlCommand player = connection.CreateCommand()) {
            player.CommandText = "SELECT id FROM players WHERE playeruid = 'uid-rollback'";
            rollbackPlayerId = Convert.ToInt64(await player.ExecuteScalarAsync());
        }
        await using (NpgsqlCommand rollback = connection.CreateCommand()) {
            rollback.CommandText = Database.RollbackBreaksQuery;
            Add(rollback, "playerid", NpgsqlDbType.Bigint, rollbackPlayerId);
            Add(rollback, "x", NpgsqlDbType.Integer, 50);
            Add(rollback, "y", NpgsqlDbType.Integer, 60);
            Add(rollback, "z", NpgsqlDbType.Integer, 70);
            Add(rollback, "radius", NpgsqlDbType.Integer, 2);
            var blocks = new List<string>();
            await using NpgsqlDataReader reader = await rollback.ExecuteReaderAsync();
            while (await reader.ReadAsync()) blocks.Add(reader.GetString(1));
            Assert.Equal(["game:block/101"], blocks);
        }

        await using (NpgsqlCommand coords = connection.CreateCommand()) {
            coords.CommandText = Database.LastEntityCoordsQuery;
            Add(coords, "entityid", NpgsqlDbType.Text, "page-target");
            await using NpgsqlDataReader reader = await coords.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((606, 70, -606), (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        }
    }

    private static async Task<List<long>> ReadIds(NpgsqlConnection connection, string sql, Action<NpgsqlCommand> configure) {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        var ids = new List<long>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task AssertCompositeIndexesAreUsable(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand setup = connection.CreateCommand();
        setup.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; SET enable_seqscan = off; SET enable_bitmapscan = off";
        await setup.ExecuteNonQueryAsync();

        string entityPlan = await Explain(connection, Database.EntityLogByEntityIdQuery, command => {
            Add(command, "entityid", NpgsqlDbType.Text, "page-target");
            Add(command, "loglimit", NpgsqlDbType.Integer, 4);
            Add(command, "skiplognum", NpgsqlDbType.Integer, 0);
        });
        Assert.Contains("idx_entitylogs_entityid_id_desc", entityPlan, StringComparison.Ordinal);

        string containerPlan = await Explain(connection, Database.ContainerLogQuery, command => {
            Add(command, "containerids", NpgsqlDbType.Array | NpgsqlDbType.Text, new[] { "container-a", "container-b" });
            Add(command, "loglimit", NpgsqlDbType.Integer, 20);
            Add(command, "skiplognum", NpgsqlDbType.Integer, 0);
        });
        Assert.Contains("idx_containerlogs_containerid_id_desc", containerPlan, StringComparison.Ordinal);
    }

    private static async Task<string> Explain(NpgsqlConnection connection, string sql, Action<NpgsqlCommand> configure) {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (COSTS OFF) " + sql;
        configure(command);
        var lines = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
    }
}
