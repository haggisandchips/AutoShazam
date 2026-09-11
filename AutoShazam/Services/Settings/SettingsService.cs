using System.IO;
using System.Text.Json;
using AutoShazam.Models;
using Microsoft.Data.Sqlite;

namespace AutoShazam.Services.Settings;

/// <summary>
/// Persists <see cref="AppSettings"/> to a single-row SQLite table rather than a flat JSON file -
/// mainly so the stored values aren't just plain text sitting in a file most editors will happily
/// open and "correct." A pre-existing settings.json from an older version is migrated in once,
/// then removed.
/// </summary>
public sealed class SettingsService
{
    private readonly string _connectionString;
    private readonly string _legacyJsonPath;

    public SettingsService(string appDataRoot)
    {
        var dbPath = Path.Combine(appDataRoot, "settings.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _legacyJsonPath = Path.Combine(appDataRoot, "settings.json");

        EnsureSchema();
    }

    public AppSettings Load()
    {
        var fromDb = TryLoadFromDb();
        if (fromDb is not null)
        {
            return fromDb;
        }

        var migrated = TryMigrateFromLegacyJson();
        if (migrated is not null)
        {
            Save(migrated);
            TryDeleteLegacyJson();
            return migrated;
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Settings
                    (Id, WindowLeft, WindowTop, WindowWidth, WindowHeight, WindowMaximized,
                     SelectedMicrophoneDeviceId, ExtendedSilenceTimeoutSeconds, SilenceThresholdDbFs,
                     SoundStateDebounceMs, AutomaticallyCheckForUpdates)
                VALUES
                    (1, $windowLeft, $windowTop, $windowWidth, $windowHeight, $windowMaximized,
                     $micId, $extendedSilenceTimeoutSeconds, $silenceThresholdDbFs,
                     $soundStateDebounceMs, $automaticallyCheckForUpdates)
                ON CONFLICT(Id) DO UPDATE SET
                    WindowLeft = excluded.WindowLeft,
                    WindowTop = excluded.WindowTop,
                    WindowWidth = excluded.WindowWidth,
                    WindowHeight = excluded.WindowHeight,
                    WindowMaximized = excluded.WindowMaximized,
                    SelectedMicrophoneDeviceId = excluded.SelectedMicrophoneDeviceId,
                    ExtendedSilenceTimeoutSeconds = excluded.ExtendedSilenceTimeoutSeconds,
                    SilenceThresholdDbFs = excluded.SilenceThresholdDbFs,
                    SoundStateDebounceMs = excluded.SoundStateDebounceMs,
                    AutomaticallyCheckForUpdates = excluded.AutomaticallyCheckForUpdates;
                """;

            AddNullableDouble(command, "$windowLeft", settings.WindowLeft);
            AddNullableDouble(command, "$windowTop", settings.WindowTop);
            command.Parameters.AddWithValue("$windowWidth", settings.WindowWidth);
            command.Parameters.AddWithValue("$windowHeight", settings.WindowHeight);
            command.Parameters.AddWithValue("$windowMaximized", settings.WindowMaximized ? 1 : 0);
            command.Parameters.AddWithValue("$micId", (object?)settings.SelectedMicrophoneDeviceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$extendedSilenceTimeoutSeconds", settings.ExtendedSilenceTimeoutSeconds);
            command.Parameters.AddWithValue("$silenceThresholdDbFs", settings.SilenceThresholdDbFs);
            command.Parameters.AddWithValue("$soundStateDebounceMs", settings.SoundStateDebounceMs);
            command.Parameters.AddWithValue("$automaticallyCheckForUpdates", settings.AutomaticallyCheckForUpdates ? 1 : 0);

            command.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort persistence only.
        }
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                WindowLeft REAL,
                WindowTop REAL,
                WindowWidth REAL NOT NULL,
                WindowHeight REAL NOT NULL,
                WindowMaximized INTEGER NOT NULL,
                SelectedMicrophoneDeviceId TEXT,
                ExtendedSilenceTimeoutSeconds REAL NOT NULL,
                SilenceThresholdDbFs REAL NOT NULL,
                SoundStateDebounceMs REAL NOT NULL,
                AutomaticallyCheckForUpdates INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private AppSettings? TryLoadFromDb()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT WindowLeft, WindowTop, WindowWidth, WindowHeight, WindowMaximized,
                       SelectedMicrophoneDeviceId, ExtendedSilenceTimeoutSeconds, SilenceThresholdDbFs,
                       SoundStateDebounceMs, AutomaticallyCheckForUpdates
                FROM Settings WHERE Id = 1;
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AppSettings
            {
                WindowLeft = reader.IsDBNull(0) ? double.NaN : reader.GetDouble(0),
                WindowTop = reader.IsDBNull(1) ? double.NaN : reader.GetDouble(1),
                WindowWidth = reader.GetDouble(2),
                WindowHeight = reader.GetDouble(3),
                WindowMaximized = reader.GetInt64(4) != 0,
                SelectedMicrophoneDeviceId = reader.IsDBNull(5) ? null : reader.GetString(5),
                ExtendedSilenceTimeoutSeconds = reader.GetDouble(6),
                SilenceThresholdDbFs = reader.GetDouble(7),
                SoundStateDebounceMs = reader.GetDouble(8),
                AutomaticallyCheckForUpdates = reader.GetInt64(9) != 0,
            };
        }
        catch
        {
            // Corrupt or unreadable database: fall back to defaults rather than crash.
            return null;
        }
    }

    private AppSettings? TryMigrateFromLegacyJson()
    {
        try
        {
            if (!File.Exists(_legacyJsonPath))
            {
                return null;
            }

            var json = File.ReadAllText(_legacyJsonPath);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    private void TryDeleteLegacyJson()
    {
        try
        {
            File.Delete(_legacyJsonPath);
        }
        catch
        {
            // Leftover legacy file is harmless - it's never read again once the DB has a row.
        }
    }

    private static void AddNullableDouble(SqliteCommand command, string name, double value)
        => command.Parameters.AddWithValue(name, double.IsNaN(value) ? DBNull.Value : value);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
