using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClipboardHistory;

internal sealed class AppSettingsRepository
{
    private readonly string _databasePath;
    private readonly DatabaseWriter _writer;

    public AppSettingsRepository(string databasePath, DatabaseWriter writer)
    {
        _databasePath = databasePath;
        _writer = writer;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 2,
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM app_settings;";

        bool enabled = AppSettings.Default.AutomaticDeletionEnabled;
        RetentionPeriod period = AppSettings.Default.RetentionPeriod;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string key = reader.GetString(0);
            string value = reader.GetString(1);
            if (string.Equals(key, "automatic_deletion_enabled", StringComparison.Ordinal))
            {
                enabled = bool.TryParse(value, out bool parsed) && parsed;
            }
            else if (string.Equals(key, "retention_period_months", StringComparison.Ordinal) &&
                     int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int months) &&
                     Enum.IsDefined(typeof(RetentionPeriod), months))
            {
                period = (RetentionPeriod)months;
            }
        }

        return new AppSettings(enabled, period);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _writer.SaveSettingsAsync(settings, cancellationToken);
}
