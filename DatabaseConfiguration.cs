using Npgsql;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GriefLedger;

internal static class DatabaseConfiguration {
    private static readonly string[] RequiredKeys = {
        "DB_HOST", "DB_PORT", "DB_NAME", "DB_USER", "DB_PASSWORD"
    };

    public static NpgsqlDataSource CreateDataSource() => CreateDataSource("/opt/app/.env");

    internal static NpgsqlDataSource CreateDataSource(string dotEnvPath) {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string key in RequiredKeys) {
            settings[key] = Environment.GetEnvironmentVariable(key);
        }
        settings["DB_SCHEMA"] = Environment.GetEnvironmentVariable("DB_SCHEMA");

        LoadMissingSettingsFromDotEnv(settings, dotEnvPath);

        string host = RequireSetting(settings, "DB_HOST");
        string database = RequireSetting(settings, "DB_NAME");
        string username = RequireSetting(settings, "DB_USER");
        string password = RequireSetting(settings, "DB_PASSWORD");
        string portText = RequireSetting(settings, "DB_PORT");
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535) {
            throw new InvalidOperationException("Database configuration contains an invalid DB_PORT.");
        }

        var builder = new NpgsqlConnectionStringBuilder {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password
        };

        string? schema = settings["DB_SCHEMA"];
        if (!string.IsNullOrWhiteSpace(schema)) {
            if (schema.Length > 63 || !Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$")) {
                throw new InvalidOperationException("Database configuration contains an invalid DB_SCHEMA.");
            }
            builder.SearchPath = "\"" + schema.Replace("\"", "\"\"") + "\"";
        }

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static string RequireSetting(Dictionary<string, string?> settings, string key) {
        if (string.IsNullOrWhiteSpace(settings[key])) {
            throw new InvalidOperationException("Database configuration is missing a required setting.");
        }
        return settings[key]!;
    }

    private static void LoadMissingSettingsFromDotEnv(Dictionary<string, string?> settings, string path) {
        if (!File.Exists(path)) return;

        foreach (string rawLine in File.ReadLines(path)) {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line.Substring(7).TrimStart();

            int separator = line.IndexOf('=');
            if (separator <= 0) continue;

            string key = line.Substring(0, separator).Trim();
            if (!settings.ContainsKey(key) || settings[key] is not null) continue;

            string value = line.Substring(separator + 1).Trim();
            if (value.Length >= 2 && ((value[0] == '\"' && value[value.Length - 1] == '\"') || (value[0] == '\'' && value[value.Length - 1] == '\''))) {
                value = value.Substring(1, value.Length - 2);
            }
            settings[key] = value;
        }
    }
}
