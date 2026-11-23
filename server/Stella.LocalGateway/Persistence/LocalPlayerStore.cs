using System.Data;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Proto;

/// <summary>
/// 本地账号与玩家存档。一个账号一个 UID、一份独立存档。
///
/// 所有写操作都走 BEGIN IMMEDIATE 事务：SQLite 只有一个写者，先拿写锁再读旧值，
/// 避免「读-改-写」在并发请求（客户端重试、两个会话同时上报）时丢掉一次修改。
/// 预留 UID 10001 给预置测试账号；新账号从 10002 起按 MAX(uid)+1 分配。
/// </summary>
internal sealed partial class LocalPlayerStore
{
    private const int SchemaVersion = 2;
    private const long FirstGeneratedUid = 10002;
    private const int BusyRetries = 6;
    private readonly string connectionString;
    private PlayerInfo? seedTemplate;

    public LocalPlayerStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public LocalAccountRecord Initialize(string seedDirectory)
    {
        Catalog = new GameCatalog(Path.Combine(seedDirectory, "..", "catalog.json"));
        seedTemplate = JsonParser.Default.Parse<PlayerInfo>(
            File.ReadAllText(Path.Combine(seedDirectory, "player-info.json")));

        using var connection = OpenConnection();
        Execute(connection, "PRAGMA journal_mode=WAL");
        Migrate(connection);
        Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "extensions.sql")));
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var account = ReadTestAccount(connection, transaction) ??
                      ImportTestAccount(connection, transaction, seedDirectory);
        transaction.Commit();
        return account;
    }

    /// <summary>v0 建库；v1 增加时间戳列。任何版本都只迁移一次，可重复执行。</summary>
    private static void Migrate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        switch ((long)command.ExecuteScalar()!)
        {
            case 0:
                Execute(connection,
                    """
                    CREATE TABLE accounts (
                        uid TEXT PRIMARY KEY NOT NULL,
                        username TEXT NOT NULL UNIQUE,
                        password_salt BLOB NOT NULL CHECK (length(password_salt) = 16),
                        password_hash BLOB NOT NULL CHECK (length(password_hash) = 32),
                        is_test INTEGER NOT NULL DEFAULT 0 CHECK (is_test IN (0, 1)),
                        created_at INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE UNIQUE INDEX one_test_account ON accounts(is_test) WHERE is_test = 1;
                    CREATE TABLE players (
                        uid TEXT PRIMARY KEY NOT NULL REFERENCES accounts(uid),
                        player_info BLOB NOT NULL,
                        updated_at INTEGER NOT NULL DEFAULT 0
                    );
                    PRAGMA user_version = 2;
                    """);
                break;
            case 1:
                Execute(connection,
                    """
                    ALTER TABLE accounts ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE players ADD COLUMN updated_at INTEGER NOT NULL DEFAULT 0;
                    PRAGMA user_version = 2;
                    """);
                break;
            case SchemaVersion:
                break;
            default:
                throw new InvalidDataException("Unsupported local gateway database schema version.");
        }
    }

    public LocalAccountRecord? FindAccount(string username) =>
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectAccount + " WHERE username = $username LIMIT 1";
            command.Parameters.AddWithValue("$username", username);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAccount(reader) : null;
        });

    public LocalAccountRecord? FindTestAccount() =>
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = SelectAccount + " WHERE is_test = 1 LIMIT 1";
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAccount(reader) : null;
        });

    /// <summary>注册账号。用户名唯一约束在事务内二次校验，并发注册只会有一个成功。</summary>
    public LocalAccountRecord CreateAccount(string username, byte[] salt, byte[] hash, long now, string? registrationCode = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                if (AccountExists(connection, transaction, username))
                {
                    throw new DuplicateAccountException(username);
                }

                using var next = connection.CreateCommand();
                next.Transaction = transaction;
                next.CommandText = "SELECT COALESCE(MAX(CAST(uid AS INTEGER)), 10001) + 1 FROM accounts";
                long allocated = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
                ulong uid = (ulong)Math.Max(allocated, FirstGeneratedUid);

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO accounts (uid, username, password_salt, password_hash, is_test, created_at)
                    VALUES ($uid, $username, $salt, $hash, 0, $created);
                    """;
                insert.Parameters.AddWithValue("$uid", uid.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$username", username);
                insert.Parameters.AddWithValue("$salt", salt);
                insert.Parameters.AddWithValue("$hash", hash);
                insert.Parameters.AddWithValue("$created", now);
                insert.ExecuteNonQuery();
                RedeemRegistrationCode(connection, transaction, uid, registrationCode, now);
                transaction.Commit();
                return new LocalAccountRecord
                {
                    Username = username,
                    Uid = uid,
                    PasswordSalt = salt,
                    PasswordHash = hash,
                    IsTest = false,
                    CreatedAt = now,
                };
            }
            catch (SqliteException error) when (IsUniqueViolation(error))
            {
                throw new DuplicateAccountException(username);
            }
            catch (SqliteException error) when (IsBusy(error) && attempt < BusyRetries)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    public bool PlayerExists(ulong uid) =>
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM players WHERE uid = $uid";
            command.Parameters.AddWithValue("$uid", FormatUid(uid));
            return command.ExecuteScalar() is not null;
        });

    public PlayerInfo? TryReadPlayer(ulong uid) =>
        WithConnection(connection => ReadPlayer(connection, null, uid));

    public PlayerInfo ReadPlayer(ulong uid) =>
        TryReadPlayer(uid) ?? throw new InvalidDataException("The account has no stored player data.");

    /// <summary>
    /// 创建角色。同一 UID 重复提交相同昵称视为重试，直接返回已建存档；
    /// 已有角色但昵称不同则拒绝，避免一个账号被并发建成两份存档。
    /// </summary>
    public PlayerInfo CreatePlayer(ulong uid, string nickname, bool male, long now)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                PlayerInfo? existing = ReadPlayer(connection, transaction, uid);
                if (existing is not null)
                {
                    if (string.Equals(existing.Acc?.NickName, nickname, StringComparison.Ordinal))
                    {
                        transaction.Commit();
                        return existing;
                    }

                    throw new PlayerAlreadyExistsException(uid);
                }

                PlayerInfo player = NewPlayer(uid, nickname, male, now);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO players (uid, player_info, updated_at) VALUES ($uid, $data, $at)";
                insert.Parameters.AddWithValue("$uid", FormatUid(uid));
                insert.Parameters.AddWithValue("$data", player.ToByteArray());
                insert.Parameters.AddWithValue("$at", now);
                insert.ExecuteNonQuery();
                transaction.Commit();
                return player;
            }
            catch (SqliteException error) when (IsBusy(error) && attempt < BusyRetries)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    /// <summary>读-改-写一个事务内完成；回调内的异常会回滚整次修改。</summary>
    public PlayerInfo UpdatePlayer(ulong uid, Action<PlayerInfo> mutate)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                PlayerInfo player = ReadPlayer(connection, transaction, uid)
                    ?? throw new InvalidDataException("The account has no stored player data.");
                mutate(player);
                player.ServerTs = 0;

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE players SET player_info = $data, updated_at = $at WHERE uid = $uid";
                update.Parameters.AddWithValue("$data", player.ToByteArray());
                update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$uid", FormatUid(uid));
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("The player row disappeared during update.");
                }

                transaction.Commit();
                return player;
            }
            catch (SqliteException error) when (IsBusy(error) && attempt < BusyRetries)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private PlayerInfo NewPlayer(ulong uid, string nickname, bool male, long now)
    {
        PlayerInfo template = seedTemplate ?? throw new InvalidDataException("Player seed template is not loaded.");
        PlayerInfo player = template.Clone();
        player.Acc ??= new AccInfo();
        player.Acc.Id = uid;
        player.Acc.NickName = nickname;
        player.Acc.Gender = male;
        player.Acc.CreateTime = now;
        player.Acc.Hashtag = (uint)(uid % 10000);
        Catalog.NormalizeProfile(player);

        // 测试环境预置引导完成；组 1 的 -1 影响后续登录，建角回调仍进入本地序章。
        player.Acc.Newbies.Clear();
        foreach (uint group in new uint[] { 1, 2, 3 })
        {
            player.Acc.Newbies.Add(new NewbieInfo { GroupId = group, StepId = -1 });
        }

        player.Board.Clear();
        foreach (uint id in template.Board)
        {
            player.Board.Add(id);
        }

        player.ServerTs = 0;
        return player;
    }

    private PlayerInfo? ReadPlayer(SqliteConnection connection, SqliteTransaction? transaction, ulong uid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT player_info FROM players WHERE uid = $uid";
        command.Parameters.AddWithValue("$uid", FormatUid(uid));
        if (command.ExecuteScalar() is not byte[] data)
        {
            return null;
        }

        PlayerInfo player = PlayerInfo.Parser.ParseFrom(data);
        if (player.Acc?.Id != uid)
        {
            throw new InvalidDataException("Stored player UID does not match its account.");
        }

        Catalog.NormalizeProfile(player);
        return player;
    }

    private static LocalAccountRecord? ReadTestAccount(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectAccount + " WHERE is_test = 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    private LocalAccountRecord ImportTestAccount(
        SqliteConnection connection, SqliteTransaction transaction, string seedDirectory)
    {
        var account = JsonSerializer.Deserialize<LocalAccountRecord>(
            File.ReadAllText(Path.Combine(seedDirectory, "account.json")));
        PlayerInfo player = JsonParser.Default.Parse<PlayerInfo>(
            File.ReadAllText(Path.Combine(seedDirectory, "player-info.json")));
        if (account is null || string.IsNullOrWhiteSpace(account.Username) || account.Uid == 0 ||
            account.Uid != player.Acc?.Id || account.PasswordSalt is not { Length: 16 } ||
            account.PasswordHash is not { Length: 32 })
        {
            throw new InvalidDataException("The local test seed has invalid credentials or a mismatched player UID.");
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO accounts (uid, username, password_salt, password_hash, is_test, created_at)
                VALUES ($uid, $username, $salt, $hash, 1, $created);
            INSERT INTO players (uid, player_info, updated_at) VALUES ($uid, $player, $created);
            """;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        insert.Parameters.AddWithValue("$uid", FormatUid(account.Uid));
        insert.Parameters.AddWithValue("$username", account.Username);
        insert.Parameters.AddWithValue("$salt", account.PasswordSalt);
        insert.Parameters.AddWithValue("$hash", account.PasswordHash);
        insert.Parameters.AddWithValue("$player", player.ToByteArray());
        insert.Parameters.AddWithValue("$created", now);
        insert.ExecuteNonQuery();
        account.IsTest = true;
        account.CreatedAt = now;
        return account;
    }

    private static bool AccountExists(SqliteConnection connection, SqliteTransaction transaction, string username)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM accounts WHERE username = $username LIMIT 1";
        command.Parameters.AddWithValue("$username", username);
        return command.ExecuteScalar() is not null;
    }

    private const string SelectAccount =
        "SELECT uid, username, password_salt, password_hash, is_test, created_at FROM accounts";

    private static LocalAccountRecord ReadAccount(SqliteDataReader reader) => new()
    {
        Uid = ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        Username = reader.GetString(1),
        PasswordSalt = reader.GetFieldValue<byte[]>(2),
        PasswordHash = reader.GetFieldValue<byte[]>(3),
        IsTest = reader.GetInt64(4) != 0,
        CreatedAt = reader.GetInt64(5),
    };

    private T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                return action(connection);
            }
            catch (SqliteException error) when (IsBusy(error) && attempt < BusyRetries)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 5000");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string FormatUid(ulong uid) => uid.ToString(CultureInfo.InvariantCulture);

    private static bool IsBusy(SqliteException error) => error.SqliteErrorCode is 5 or 6;

    private static bool IsUniqueViolation(SqliteException error) =>
        error.SqliteErrorCode == 19 && error.Message.Contains("accounts.username", StringComparison.OrdinalIgnoreCase);
}

internal sealed class DuplicateAccountException(string username)
    : Exception($"The local account '{username}' already exists.");

internal sealed class PlayerAlreadyExistsException(ulong uid)
    : Exception($"UID {uid} already has a stored character.");

internal sealed class LocalAccountRecord
{
    public string Username { get; set; } = "";
    public ulong Uid { get; set; }
    public byte[] PasswordSalt { get; set; } = [];
    public byte[] PasswordHash { get; set; } = [];
    public bool IsTest { get; set; }
    public long CreatedAt { get; set; }
}
