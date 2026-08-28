using Npgsql;
using Xunit;

namespace GriefLedger.ReflectionTests;

public sealed class DatabaseConfigurationTests {
    private static readonly string[] ConfigurationKeys = [
        "DB_HOST", "DB_PORT", "DB_NAME", "DB_USER", "DB_PASSWORD", "DB_SCHEMA",
        "DB_SSL_MODE", "DB_SSL_ROOT_CERTIFICATE"
    ];

    [Theory]
    [InlineData("Disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("Prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("VerifyCA", SslMode.VerifyCA)]
    [InlineData("verifyfull", SslMode.VerifyFull)]
    public void Ssl_mode_parser_accepts_documented_npgsql_values_case_insensitively(
        string value, SslMode expected) {
        Assert.Equal(expected, DatabaseConfiguration.ParseSslMode(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("TrustEverything")]
    [InlineData("Verify Full")]
    public void Ssl_mode_parser_rejects_blank_numeric_and_unknown_values(string value) {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => DatabaseConfiguration.ParseSslMode(value));

        Assert.Equal("Database configuration contains an invalid DB_SSL_MODE.", error.Message);
    }

    [Fact]
    public void Optional_tls_settings_use_process_precedence_and_dotenv_fallback_independently() {
        var original = ConfigurationKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        string dotEnvPath = Path.GetTempFileName();
        try {
            Environment.SetEnvironmentVariable("DB_HOST", "database.invalid");
            Environment.SetEnvironmentVariable("DB_PORT", "5432");
            Environment.SetEnvironmentVariable("DB_NAME", "griefledger_test");
            Environment.SetEnvironmentVariable("DB_USER", "griefledger_test");
            Environment.SetEnvironmentVariable("DB_PASSWORD", "not-a-real-secret");
            Environment.SetEnvironmentVariable("DB_SCHEMA", null);
            File.WriteAllText(dotEnvPath,
                "DB_SSL_MODE=VerifyCA\nDB_SSL_ROOT_CERTIFICATE=/dotenv/root.pem\n");

            Environment.SetEnvironmentVariable("DB_SSL_MODE", "VerifyFull");
            Environment.SetEnvironmentVariable("DB_SSL_ROOT_CERTIFICATE", "/environment/root.pem");
            NpgsqlConnectionStringBuilder environment =
                DatabaseConfiguration.CreateConnectionStringBuilder(dotEnvPath);
            Assert.Equal(SslMode.VerifyFull, environment.SslMode);
            Assert.Equal("/environment/root.pem", environment.RootCertificate);

            Environment.SetEnvironmentVariable("DB_SSL_MODE", "Disable");
            Environment.SetEnvironmentVariable("DB_SSL_ROOT_CERTIFICATE", null);
            NpgsqlConnectionStringBuilder mixed = DatabaseConfiguration.CreateConnectionStringBuilder(dotEnvPath);
            Assert.Equal(SslMode.Disable, mixed.SslMode);
            Assert.Equal("/dotenv/root.pem", mixed.RootCertificate);

            Environment.SetEnvironmentVariable("DB_SSL_MODE", null);
            NpgsqlConnectionStringBuilder dotEnv = DatabaseConfiguration.CreateConnectionStringBuilder(dotEnvPath);
            Assert.Equal(SslMode.VerifyCA, dotEnv.SslMode);
            Assert.Equal("/dotenv/root.pem", dotEnv.RootCertificate);

            File.WriteAllText(dotEnvPath, string.Empty);
            Environment.SetEnvironmentVariable("DB_SSL_ROOT_CERTIFICATE", null);
            NpgsqlConnectionStringBuilder defaults = DatabaseConfiguration.CreateConnectionStringBuilder(dotEnvPath);
            Assert.Equal(SslMode.Prefer, defaults.SslMode);
            Assert.Null(defaults.RootCertificate);
        }
        finally {
            foreach ((string key, string? value) in original) Environment.SetEnvironmentVariable(key, value);
            File.Delete(dotEnvPath);
        }
    }
}
