using System.Text.Json;
using GalgameManager.Application;
using Microsoft.Data.Sqlite;

namespace GalgameManager.Infrastructure;

public sealed class SqliteLibrary(string databasePath, DiagnosticLog log) : IGameRepository, IScanRootRepository
{
    private const int SchemaVersion = 2;
    private readonly object writeGate = new();

    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        ForeignKeys = true,
        DefaultTimeout = 3
    }.ToString();

    public void Initialize() => Write(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var readVersion = Command(connection, transaction, "PRAGMA user_version");
        var version = (long)readVersion.ExecuteScalar()!;
        if (version > SchemaVersion)
            throw new LibraryException("游戏库由更新版本的管理器创建。请使用对应版本打开此库。");
        if (version == 0)
        {
            using var migrate = Command(connection, transaction, """
                CREATE TABLE scan_roots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL CHECK (length(path) > 0),
                    path_key TEXT NOT NULL UNIQUE CHECK (length(path_key) > 0)
                );
                CREATE TABLE games (
                    id INTEGER PRIMARY KEY,
                    source_root_id INTEGER REFERENCES scan_roots(id) ON DELETE SET NULL,
                    display_name TEXT NOT NULL CHECK (length(trim(display_name)) > 0),
                    install_dir TEXT NOT NULL CHECK (length(install_dir) > 0),
                    install_key TEXT NOT NULL UNIQUE CHECK (length(install_key) > 0),
                    executable_rel TEXT CHECK (executable_rel IS NULL OR length(executable_rel) > 0),
                    executable_key TEXT UNIQUE,
                    work_dir_override TEXT,
                    args_json TEXT NOT NULL DEFAULT '[]',
                    updated_at_utc TEXT NOT NULL
                );
                CREATE INDEX ix_games_source_root ON games(source_root_id);
                PRAGMA user_version = 1;
                """);
            migrate.ExecuteNonQuery();
        }
        if (version < 2)
        {
            using var migrate = Command(connection, transaction, """
                ALTER TABLE games ADD COLUMN summary TEXT;
                ALTER TABLE games ADD COLUMN cover_path TEXT;
                ALTER TABLE games ADD COLUMN metadata_provider TEXT;
                ALTER TABLE games ADD COLUMN metadata_id TEXT;
                PRAGMA user_version = 2;
                """);
            migrate.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }, "initialize-library", createDirectory: true);

    public IReadOnlyList<ScanRoot> ListRoots() => Access<IReadOnlyList<ScanRoot>>(connection =>
    {
        using var command = Command(connection, null, "SELECT id, path FROM scan_roots ORDER BY path_key");
        using var reader = command.ExecuteReader();
        var result = new List<ScanRoot>();
        while (reader.Read())
            result.Add(new ScanRoot(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }, "list-scan-roots");

    public void AddRoots(IReadOnlyList<string> paths) => Write(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var path in paths)
        {
            using var command = Command(connection, transaction,
                "INSERT INTO scan_roots(path, path_key) VALUES ($path, $key) ON CONFLICT(path_key) DO NOTHING",
                ("$path", path), ("$key", LocalPaths.Key(path)));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }, "add-scan-roots");

    public void RemoveRoot(long id) => Write(connection =>
    {
        using var command = Command(connection, null, "DELETE FROM scan_roots WHERE id = $id", ("$id", id));
        return command.ExecuteNonQuery();
    }, "remove-scan-root");

    private const string SelectGame = """
        SELECT id, source_root_id, display_name, install_dir, executable_rel, work_dir_override, args_json,
            summary, cover_path, metadata_provider, metadata_id FROM games
        """;

    public IReadOnlyList<Game> ListGames() => Access<IReadOnlyList<Game>>(connection =>
    {
        using var command = Command(connection, null, SelectGame + " ORDER BY id");
        using var reader = command.ExecuteReader();
        var result = new List<Game>();
        while (reader.Read())
            result.Add(ReadGame(reader));
        return result;
    }, "list-games");

    public Game? FindGame(long id) => Access(connection =>
    {
        using var command = Command(connection, null, SelectGame + " WHERE id = $id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGame(reader) : null;
    }, "read-game");

    public void SaveGame(Game game) => Write(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var conflict = FindConflict(connection, transaction, game);
        if (conflict is { } existing)
            throw new LibraryException($"此安装目录或启动入口已用于「{existing.Name}」。\n{existing.Path}");

        using var command = game.Id == 0 ? Insert(connection, transaction, game) : Command(connection, transaction, """
            UPDATE games SET display_name = $name, install_dir = $install, install_key = $installKey,
                executable_rel = $entry, executable_key = $entryKey, work_dir_override = $work,
                args_json = $arguments, updated_at_utc = $updated,
                summary = CASE WHEN $hasMetadata THEN $summary ELSE summary END,
                cover_path = CASE WHEN $hasMetadata THEN $cover ELSE cover_path END,
                metadata_provider = CASE WHEN $hasMetadata THEN $provider ELSE metadata_provider END,
                metadata_id = CASE WHEN $hasMetadata THEN $metadataId ELSE metadata_id END
            WHERE id = $id
            """, Parameters(game).Append(("$id", (object?)game.Id))
                .Append(("$hasMetadata", (object?)(game.Metadata is not null))).ToArray());
        if (command.ExecuteNonQuery() != 1)
            throw new LibraryException("游戏记录已不存在，请刷新游戏库。");
        transaction.Commit();
        return true;
    }, "save-game");

    public void RemoveGame(long id) => Write(connection =>
    {
        using var command = Command(connection, null, "DELETE FROM games WHERE id = $id", ("$id", id));
        return command.ExecuteNonQuery();
    }, "remove-game");

    public ImportResult ImportGames(IReadOnlyList<Game> games) => Write(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var game in games)
        {
            if (FindConflict(connection, transaction, game) is not null)
                continue;

            using var command = Insert(connection, transaction, game);
            added += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return new ImportResult(added, games.Count - added);
    }, "import-games");

    private static SqliteCommand Insert(SqliteConnection connection, SqliteTransaction transaction, Game game) =>
        Command(connection, transaction, """
            INSERT INTO games(source_root_id, display_name, install_dir, install_key, executable_rel,
                executable_key, work_dir_override, args_json, updated_at_utc,
                summary, cover_path, metadata_provider, metadata_id)
            VALUES ((SELECT id FROM scan_roots WHERE id = $source), $name, $install, $installKey,
                $entry, $entryKey, $work, $arguments, $updated, $summary, $cover, $provider, $metadataId)
            """, Parameters(game).Append(("$source", (object?)game.SourceRootId)).ToArray());

    private static (string, object?)[] Parameters(Game game) =>
    [
        ("$name", game.DisplayName), ("$install", game.InstallDirectory), ("$installKey", LocalPaths.Key(game.InstallDirectory)),
        ("$entry", game.RelativeExecutable), ("$entryKey", game.Executable is { } entry ? LocalPaths.Key(entry) : null),
        ("$work", game.WorkDirectory), ("$arguments", JsonSerializer.Serialize(game.Arguments)),
        ("$updated", DateTimeOffset.UtcNow.ToString("O")),
        ("$summary", game.Metadata?.Summary), ("$cover", game.Metadata?.CoverPath),
        ("$provider", game.Metadata?.Provider), ("$metadataId", game.Metadata?.ProviderId)
    ];

    private static (string Name, string Path)? FindConflict(SqliteConnection connection, SqliteTransaction transaction, Game game)
    {
        using var command = Command(connection, transaction, """
            SELECT display_name, install_dir FROM games
            WHERE id <> $id AND (install_key = $install OR executable_key = $entry)
            LIMIT 1
            """, ("$id", game.Id), ("$install", LocalPaths.Key(game.InstallDirectory)),
            ("$entry", game.Executable is { } entry ? LocalPaths.Key(entry) : null));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static Game ReadGame(SqliteDataReader reader) => new(reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
        new GameMetadata(OptionalString(reader, 7), OptionalString(reader, 8),
            OptionalString(reader, 9), OptionalString(reader, 10)));

    private static string? OptionalString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in parameters)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    private T Write<T>(Func<SqliteConnection, T> action, string operation, bool createDirectory = false)
    {
        lock (writeGate)
            return Access(action, operation, createDirectory);
    }

    private T Access<T>(Func<SqliteConnection, T> action, string operation, bool createDirectory = false)
    {
        try
        {
            if (createDirectory)
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return action(connection);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write(operation, ex);
            var reason = ex is SqliteException sqlite ? sqlite.SqliteErrorCode switch
            {
                5 or 6 => "数据库正在使用，请稍后重试。",
                8 or 14 => "无法打开或写入数据库，请检查本地应用数据目录权限。",
                11 or 26 => "数据库文件无法读取，请从可用备份恢复。",
                13 => "本地磁盘空间不足，请释放空间后重试。",
                _ => "数据库操作失败，请重试或查看本地诊断日志。"
            } : "无法读取或保存游戏库，请检查本地应用数据目录及诊断日志。";
            throw new LibraryException(reason);
        }
    }
}
