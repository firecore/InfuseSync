using InfuseSync.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfuseSync.Tests.Storage;

public sealed class SnapshotSourceIndexTests : IDisposable
{
    private const string ItemsIndex = "idx_items_modified";
    private const string UserInfoIndex = "idx_user_info_user_modified";

    private readonly string _databaseDirectory = Path.Combine(
        Path.GetTempPath(),
        "InfuseSync.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingDatabase_AddsIndexesWithoutChangingSchemaVersionOrData()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var databasePath = DatabasePath;

        using (var connection = Open(databasePath))
        {
            Execute(
                connection,
                """
                PRAGMA user_version = 4;
                create table checkpoints (Guid BLOB PRIMARY KEY, DeviceId TEXT NOT NULL, UserId TEXT NOT NULL, Timestamp INTEGER NOT NULL, SyncTimestamp INTEGER NULL, LastActivity INTEGER NOT NULL);
                create table items (Guid BLOB PRIMARY KEY, SeriesId BLOB NULL, Season INTEGER NULL, Status INTEGER NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL);
                create table user_info (Guid BLOB NOT NULL, UserId TEXT NOT NULL, LastModified INTEGER NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Guid, UserId));
                create index idx_items on items(Guid);
                create index idx_user_info on user_info(Guid, UserId);
                insert into items values (randomblob(16), NULL, NULL, 0, 100, 'Movie');
                insert into user_info values (randomblob(16), 'user-1', 100, 'Movie');
                """);
        }

        using (var database = new Db(_databaseDirectory, NullLogger.Instance))
        {
        }

        using var initialized = Open(databasePath);
        Assert.Equal(4L, Scalar<long>(initialized, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar<long>(initialized, "select COUNT(*) from items;"));
        Assert.Equal(1L, Scalar<long>(initialized, "select COUNT(*) from user_info;"));
        Assert.True(IndexExists(initialized, ItemsIndex));
        Assert.True(IndexExists(initialized, UserInfoIndex));
        Assert.False(IndexExists(initialized, "idx_items"));
        Assert.False(IndexExists(initialized, "idx_user_info"));
    }

    [Fact]
    public void SnapshotQueriesAndItemRetention_UseSourceIndexes()
    {
        using (var database = new Db(_databaseDirectory, NullLogger.Instance))
        {
        }

        using var connection = Open(DatabasePath);

        Assert.Contains(
            ItemsIndex,
            QueryPlan(
                connection,
                "select * from items where LastModified between 10 and 20;"));
        Assert.Contains(
            UserInfoIndex,
            QueryPlan(
                connection,
                "select * from user_info where UserId = 'user-1' and LastModified between 10 and 20;"));
        Assert.Contains(
            "sqlite_autoindex_items_1",
            QueryPlan(connection, "select * from items where Guid = zeroblob(16);"));
        Assert.Contains(
            "sqlite_autoindex_user_info_1",
            QueryPlan(connection, "select * from user_info where Guid = zeroblob(16) and UserId = 'user-1';"));
        Assert.Contains(
            ItemsIndex,
            QueryPlan(connection, "delete from items where LastModified < 10;"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_databaseDirectory))
        {
            Directory.Delete(_databaseDirectory, true);
        }
    }

    private string DatabasePath => Path.Combine(_databaseDirectory, "infuse_sync.db");

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static bool IndexExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from sqlite_master where type = 'index' and name = @Name);";
        command.Parameters.AddWithValue("@Name", name);
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    private static string QueryPlan(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, details);
    }
}
