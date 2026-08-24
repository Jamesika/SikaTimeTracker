using System.Globalization;
using Microsoft.Data.Sqlite;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Infrastructure.Data;

public sealed class SqliteActivityStore : IActivityStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteActivityStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL COLLATE NOCASE,
                Color TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                IsDefault INTEGER NOT NULL DEFAULT 0 CHECK (IsDefault IN (0, 1))
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_Categories_Name ON Categories(Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Categories_Default
                ON Categories(IsDefault) WHERE IsDefault = 1;

            CREATE TABLE IF NOT EXISTS ClassificationRules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CategoryId INTEGER NOT NULL,
                Target INTEGER NOT NULL,
                MatchType INTEGER NOT NULL,
                Pattern TEXT NOT NULL,
                IgnoreCase INTEGER NOT NULL DEFAULT 1 CHECK (IgnoreCase IN (0, 1)),
                Priority INTEGER NOT NULL DEFAULT 0,
                IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_ClassificationRules_Priority
                ON ClassificationRules(IsEnabled, Priority DESC);

            CREATE TABLE IF NOT EXISTS ActivitySegments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartTimeUtc TEXT NOT NULL,
                EndTimeUtc TEXT NULL,
                LastHeartbeatUtc TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL,
                CategoryId INTEGER NOT NULL,
                ClassificationRuleId INTEGER NULL,
                IsManuallyClassified INTEGER NOT NULL DEFAULT 0 CHECK (IsManuallyClassified IN (0, 1)),
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE RESTRICT,
                FOREIGN KEY (ClassificationRuleId) REFERENCES ClassificationRules(Id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ActivitySegments_Time
                ON ActivitySegments(StartTimeUtc, EndTimeUtc);
            CREATE INDEX IF NOT EXISTS IX_ActivitySegments_CategoryTime
                ON ActivitySegments(CategoryId, StartTimeUtc);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ActivitySegments_OneOpen
                ON ActivitySegments((1)) WHERE EndTimeUtc IS NULL;

            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            INSERT OR IGNORE INTO Categories(Id, Name, Color, SortOrder, IsDefault)
                VALUES (1, '其他', '#8A8886', 999, 1);
            INSERT OR IGNORE INTO Categories(Id, Name, Color, SortOrder, IsDefault)
                VALUES (2, '工作', '#4F6BED', 10, 0);
            INSERT OR IGNORE INTO Categories(Id, Name, Color, SortOrder, IsDefault)
                VALUES (3, '游戏', '#C239B3', 20, 0);

            PRAGMA user_version = {{CurrentSchemaVersion}};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = new List<Category>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Color, SortOrder, IsDefault FROM Categories ORDER BY SortOrder, Id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            categories.Add(new Category(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetBoolean(4)));
        }

        return categories;
    }

    public async Task<Category> SaveCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category.Color);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        AddParameter(command, "$name", category.Name.Trim());
        AddParameter(command, "$color", category.Color);
        AddParameter(command, "$sortOrder", category.SortOrder);
        AddParameter(command, "$isDefault", category.IsDefault);

        if (category.Id == 0)
        {
            command.CommandText = """
                INSERT INTO Categories(Name, Color, SortOrder, IsDefault)
                VALUES ($name, $color, $sortOrder, $isDefault);
                SELECT last_insert_rowid();
                """;
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return category with { Id = id, Name = category.Name.Trim() };
        }

        AddParameter(command, "$id", category.Id);
        command.CommandText = """
            UPDATE Categories
            SET Name = $name, Color = $color, SortOrder = $sortOrder, IsDefault = $isDefault
            WHERE Id = $id;
            """;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            throw new InvalidOperationException($"分类 {category.Id} 不存在");
        }

        return category with { Name = category.Name.Trim() };
    }

    public async Task<IReadOnlyList<ClassificationRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = new List<ClassificationRule>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CategoryId, Target, MatchType, Pattern, IgnoreCase, Priority, IsEnabled
            FROM ClassificationRules
            ORDER BY Priority DESC, Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new ClassificationRule(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (RuleTarget)reader.GetInt32(2),
                (RuleMatchType)reader.GetInt32(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetInt32(6),
                reader.GetBoolean(7)));
        }

        return rules;
    }

    public async Task<ClassificationRule> SaveRuleAsync(ClassificationRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Pattern);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        AddParameter(command, "$categoryId", rule.CategoryId);
        AddParameter(command, "$target", (int)rule.Target);
        AddParameter(command, "$matchType", (int)rule.MatchType);
        AddParameter(command, "$pattern", rule.Pattern);
        AddParameter(command, "$ignoreCase", rule.IgnoreCase);
        AddParameter(command, "$priority", rule.Priority);
        AddParameter(command, "$isEnabled", rule.IsEnabled);

        if (rule.Id == 0)
        {
            command.CommandText = """
                INSERT INTO ClassificationRules(
                    CategoryId, Target, MatchType, Pattern, IgnoreCase, Priority, IsEnabled)
                VALUES (
                    $categoryId, $target, $matchType, $pattern, $ignoreCase, $priority, $isEnabled);
                SELECT last_insert_rowid();
                """;
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return rule with { Id = id };
        }

        AddParameter(command, "$id", rule.Id);
        command.CommandText = """
            UPDATE ClassificationRules
            SET CategoryId = $categoryId,
                Target = $target,
                MatchType = $matchType,
                Pattern = $pattern,
                IgnoreCase = $ignoreCase,
                Priority = $priority,
                IsEnabled = $isEnabled
            WHERE Id = $id;
            """;
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            throw new InvalidOperationException($"规则 {rule.Id} 不存在");
        }

        return rule;
    }

    public async Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ClassificationRules WHERE Id = $id;";
        AddParameter(command, "$id", ruleId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> StartActivityAsync(ActivityDraft activity, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySegments(
                StartTimeUtc, EndTimeUtc, LastHeartbeatUtc, ProcessName, WindowTitle,
                CategoryId, ClassificationRuleId, IsManuallyClassified, CreatedAtUtc)
            VALUES (
                $startTimeUtc, NULL, $startTimeUtc, $processName, $windowTitle,
                $categoryId, $ruleId, $isManual, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$startTimeUtc", FormatUtc(activity.StartTimeUtc));
        AddParameter(command, "$processName", activity.ProcessName);
        AddParameter(command, "$windowTitle", activity.WindowTitle);
        AddParameter(command, "$categoryId", activity.CategoryId);
        AddParameter(command, "$ruleId", activity.ClassificationRuleId);
        AddParameter(command, "$isManual", activity.IsManuallyClassified);
        AddParameter(command, "$createdAtUtc", FormatUtc(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public Task<bool> UpdateHeartbeatAsync(
        long activityId,
        DateTimeOffset heartbeatUtc,
        CancellationToken cancellationToken = default)
    {
        return ExecuteOpenActivityUpdateAsync(
            activityId,
            "UPDATE ActivitySegments SET LastHeartbeatUtc = $timeUtc WHERE Id = $id AND EndTimeUtc IS NULL;",
            heartbeatUtc,
            cancellationToken);
    }

    public Task<bool> StopActivityAsync(
        long activityId,
        DateTimeOffset endTimeUtc,
        CancellationToken cancellationToken = default)
    {
        return ExecuteOpenActivityUpdateAsync(
            activityId,
            """
            UPDATE ActivitySegments
            SET EndTimeUtc = $timeUtc, LastHeartbeatUtc = $timeUtc
            WHERE Id = $id AND EndTimeUtc IS NULL;
            """,
            endTimeUtc,
            cancellationToken);
    }

    public async Task<int> RecoverOpenActivitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ActivitySegments
            SET EndTimeUtc = LastHeartbeatUtc
            WHERE EndTimeUtc IS NULL;
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivitySegment>> GetActivitiesAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken cancellationToken = default)
    {
        if (rangeEndUtc <= rangeStartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEndUtc), "结束时间必须晚于开始时间");
        }

        var activities = new List<ActivitySegment>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, StartTimeUtc, EndTimeUtc, LastHeartbeatUtc, ProcessName, WindowTitle,
                   CategoryId, ClassificationRuleId, IsManuallyClassified
            FROM ActivitySegments
            WHERE StartTimeUtc < $rangeEndUtc
              AND COALESCE(EndTimeUtc, LastHeartbeatUtc) > $rangeStartUtc
            ORDER BY StartTimeUtc, Id;
            """;
        AddParameter(command, "$rangeStartUtc", FormatUtc(rangeStartUtc));
        AddParameter(command, "$rangeEndUtc", FormatUtc(rangeEndUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            activities.Add(new ActivitySegment(
                reader.GetInt64(0),
                ParseUtc(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
                ParseUtc(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetBoolean(8)));
        }

        return activities;
    }

    public async Task<bool> UpdateActivityClassificationAsync(
        long activityId,
        long categoryId,
        long? ruleId,
        bool isManual,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ActivitySegments
            SET CategoryId = $categoryId,
                ClassificationRuleId = $ruleId,
                IsManuallyClassified = $isManual
            WHERE Id = $id;
            """;
        AddParameter(command, "$categoryId", categoryId);
        AddParameter(command, "$ruleId", ruleId);
        AddParameter(command, "$isManual", isManual);
        AddParameter(command, "$id", activityId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
        AddParameter(command, "$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings(Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        AddParameter(command, "$key", key);
        AddParameter(command, "$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> ExecuteOpenActivityUpdateAsync(
        long activityId,
        string commandText,
        DateTimeOffset timeUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameter(command, "$id", activityId);
        AddParameter(command, "$timeUtc", FormatUtc(timeUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
