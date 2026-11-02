using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Wtss.PamBrokerEnrollmentService;

public sealed class EnrollmentStateStore(IOptions<EnrollmentOptions> options)
{
    private readonly string _connectionString = BuildConnectionString(options.Value.StateDatabasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Enrollments (
                AccountId INTEGER NOT NULL,
                PluginName TEXT NOT NULL,
                AccountName TEXT NOT NULL,
                AssetName TEXT NOT NULL,
                State TEXT NOT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastObservedUtc TEXT NOT NULL,
                LastAppliedUtc TEXT NULL,
                LastError TEXT NULL,
                PRIMARY KEY (AccountId, PluginName)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ObserveAsync(
        IEnumerable<SafeguardAccount> accounts,
        string pluginName,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var account in accounts)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO Enrollments
                    (AccountId, PluginName, AccountName, AssetName, State, FirstSeenUtc, LastObservedUtc)
                VALUES ($id, $plugin, $account, $asset, 'Observed', $now, $now)
                ON CONFLICT(AccountId, PluginName) DO UPDATE SET
                    AccountName = excluded.AccountName,
                    AssetName = excluded.AssetName,
                    LastObservedUtc = excluded.LastObservedUtc;
                """;
            command.Parameters.AddWithValue("$id", account.Id);
            command.Parameters.AddWithValue("$plugin", pluginName);
            command.Parameters.AddWithValue("$account", account.Name);
            command.Parameters.AddWithValue("$asset", account.Asset.Name);
            command.Parameters.AddWithValue("$now", observedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkEnrolledAsync(
        IEnumerable<SafeguardAccount> accounts,
        string pluginName,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var account in accounts)
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Enrollments
                SET State = 'Enrolled', LastAppliedUtc = $now, LastError = NULL
                WHERE AccountId = $id AND PluginName = $plugin;
                """;
            command.Parameters.AddWithValue("$now", appliedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", account.Id);
            command.Parameters.AddWithValue("$plugin", pluginName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlySet<int>> GetOwnedAccountIdsAsync(
        string pluginName,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<int>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT AccountId FROM Enrollments WHERE PluginName = $plugin AND State = 'Enrolled';";
        command.Parameters.AddWithValue("$plugin", pluginName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private static string BuildConnectionString(string configuredPath)
    {
        var fullPath = Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("StateDatabasePath has no parent directory."));
        return new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }
}

