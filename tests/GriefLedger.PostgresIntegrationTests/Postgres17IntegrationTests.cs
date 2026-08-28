using System.Text;
using GriefLedger;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace GriefLedger.PostgresIntegrationTests;

public sealed class Postgres17IntegrationTests {
    private static readonly string[] RequiredSettings = ["DB_HOST", "DB_PORT", "DB_NAME", "DB_USER", "DB_PASSWORD"];

    [Fact]
    public async Task Production_database_paths_work_on_postgresql_17_11() {
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

            await AssertPostgres17_11(admin);
            await AssertSchemaConfigurationPrecedence(schema);
            BootstrapRepeatedly();
            await AssertBootstrapSchema(admin, schema);

            await RestartIdentitiesAboveIntMax(admin, schema);
            await AddFailureConstraint(admin, schema);
            await ExerciseConcurrentPlayerUpsert();
            ExerciseWritesCompressionFailureAndDrain();
            ExerciseFinalPlayerNameUpdate();

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

    private static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static async Task AssertPostgres17_11(NpgsqlDataSource admin) {
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
        columns.CommandText = "SELECT table_name, column_name, data_type, is_identity FROM information_schema.columns WHERE table_schema = @schema ORDER BY table_name, ordinal_position";
        columns.Parameters.AddWithValue("schema", schema);
        var rows = new List<(string Table, string Column, string Type, string Identity)>();
        await using (NpgsqlDataReader reader = await columns.ExecuteReaderAsync()) {
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        Assert.Equal(4, rows.Select(row => row.Table).Distinct().Count());
        Assert.All(rows.Where(row => row.Column == "id"), row => {
            Assert.Equal("bigint", row.Type);
            Assert.Equal("YES", row.Identity);
        });
        Assert.Contains(rows, row => row.Column == "itemstack_data" && row.Type == "bytea");

        await using NpgsqlCommand indexes = connection.CreateCommand();
        indexes.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = @schema";
        indexes.Parameters.AddWithValue("schema", schema);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (NpgsqlDataReader reader = await indexes.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        string[] expected = [
            "idx_blocklogs_coords", "idx_entitylogs_coords", "idx_containerlogs_id",
            "idx_entitylogs_entityid_id_desc", "idx_containerlogs_containerid_id_desc",
            "idx_blocklogs_ts", "idx_blocklogs_pid_ts", "idx_blocklogs_act_ts",
            "idx_entitylogs_ts", "idx_entitylogs_pid_ts", "idx_entitylogs_act_ts",
            "idx_containerlogs_ts", "idx_containerlogs_pid_ts", "idx_containerlogs_act_ts"
        ];
        Assert.All(expected, name => Assert.Contains(name, names));
    }

    private static async Task RestartIdentitiesAboveIntMax(NpgsqlDataSource admin, string schema) {
        await using NpgsqlConnection connection = await admin.OpenConnectionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO {QuoteIdentifier(schema)}; ALTER TABLE players ALTER COLUMN id RESTART WITH 2147483650;";
        await command.ExecuteNonQueryAsync();
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
