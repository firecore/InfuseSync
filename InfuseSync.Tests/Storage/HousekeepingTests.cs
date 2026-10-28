using InfuseSync.Models;
using InfuseSync.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfuseSync.Tests.Storage;

public sealed class HousekeepingTests : IDisposable
{
    private readonly string _databaseDirectory;
    private readonly string _databasePath;
    private readonly Db _database;

    public HousekeepingTests()
    {
        _databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "InfuseSync.Tests",
            Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_databaseDirectory, "infuse_sync.db");
        _database = new Db(_databaseDirectory, NullLogger.Instance);
    }

    [Fact]
    public void DeleteOldData_UsesOldestSurvivingCheckpointAsRetentionBoundary()
    {
        var expiredCheckpoint = InsertCheckpoint(50);
        var oldestCheckpoint = InsertCheckpoint(200);
        var newestCheckpoint = InsertCheckpoint(250);

        _database.SaveItems(
            new[]
            {
                Item(99),
                Item(150),
                Item(199),
                Item(200),
                Item(250)
            });
        _database.SaveUserInfo(
            new List<UserInfoRec>
            {
                UserInfo(99),
                UserInfo(150),
                UserInfo(199),
                UserInfo(200),
                UserInfo(250)
            });

        _database.DeleteOldData(100);

        Assert.Null(_database.GetCheckpoint(expiredCheckpoint));
        Assert.NotNull(_database.GetCheckpoint(oldestCheckpoint));
        Assert.NotNull(_database.GetCheckpoint(newestCheckpoint));
        Assert.Equal(
            new long[] { 200, 250 },
            ReadTimestamps("items"));
        Assert.Equal(
            new long[] { 200, 250 },
            ReadTimestamps("user_info"));
    }

    [Fact]
    public void DeleteOldData_ClearsChangeDataWhenNoCheckpointSurvives()
    {
        var expiredCheckpoint = InsertCheckpoint(50);
        _database.SaveItems(new[] { Item(200) });
        _database.SaveUserInfo(new List<UserInfoRec> { UserInfo(200) });

        _database.DeleteOldData(100);

        Assert.Null(_database.GetCheckpoint(expiredCheckpoint));
        Assert.Empty(ReadTimestamps("items"));
        Assert.Empty(ReadTimestamps("user_info"));
    }

    [Fact]
    public void DeleteOldData_RemovesOnlyExpiredCheckpointSnapshots()
    {
        var expiredCheckpoint = InsertCheckpoint(50);
        var activeCheckpoint = InsertCheckpoint(200);
        InsertCheckpointItem(expiredCheckpoint, 50);
        InsertCheckpointItem(activeCheckpoint, 200);

        _database.DeleteOldData(100);

        Assert.Equal(0, SnapshotItemCount(expiredCheckpoint));
        Assert.Equal(1, SnapshotItemCount(activeCheckpoint));
    }

    [Fact]
    public void DeleteOldData_KeepsRecentlyActiveCheckpointWithOldCursor()
    {
        var activeCheckpoint = InsertCheckpoint(50, 200);
        InsertCheckpointItem(activeCheckpoint, 75);

        _database.DeleteOldData(100);

        Assert.NotNull(_database.GetCheckpoint(activeCheckpoint));
        Assert.Equal(1, SnapshotItemCount(activeCheckpoint));
    }

    [Fact]
    public void DeleteOldData_UsesCompletedSnapshotCutoffForLiveRetention()
    {
        var activeCheckpoint = InsertCheckpoint(50, 200, 200);
        InsertCheckpointItem(activeCheckpoint, 75);
        _database.SaveItems(new[] { Item(50), Item(199), Item(200), Item(250) });
        _database.SaveUserInfo(
            new List<UserInfoRec> { UserInfo(50), UserInfo(199), UserInfo(200), UserInfo(250) });

        _database.DeleteOldData(100);

        Assert.NotNull(_database.GetCheckpoint(activeCheckpoint));
        Assert.Equal(1, SnapshotItemCount(activeCheckpoint));
        Assert.Equal(new long[] { 200, 250 }, ReadTimestamps("items"));
        Assert.Equal(new long[] { 200, 250 }, ReadTimestamps("user_info"));
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_databaseDirectory, true);
    }

    private Guid InsertCheckpoint(
        long timestamp,
        long? lastActivity = null,
        long? syncTimestamp = null)
    {
        var checkpointId = Guid.NewGuid();
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "insert into checkpoints(Guid, DeviceId, UserId, Timestamp, SyncTimestamp, LastActivity) " +
            "values (@Guid, @DeviceId, @UserId, @Timestamp, @SyncTimestamp, @LastActivity);";
        command.Parameters.Add("@Guid", SqliteType.Blob).Value = checkpointId.ToByteArray();
        command.Parameters.AddWithValue("@DeviceId", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@UserId", "user-1");
        command.Parameters.AddWithValue("@Timestamp", timestamp);
        command.Parameters.AddWithValue("@SyncTimestamp", (object)syncTimestamp ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastActivity", lastActivity ?? timestamp);
        command.ExecuteNonQuery();
        return checkpointId;
    }

    private void InsertCheckpointItem(Guid checkpointId, long timestamp)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "insert into checkpoint_items(CheckpointId, Guid, SeriesId, Season, Status, LastModified, Type) " +
            "values (@CheckpointId, @Guid, NULL, NULL, @Status, @LastModified, @Type);";
        command.Parameters.Add("@CheckpointId", SqliteType.Blob).Value = checkpointId.ToByteArray();
        command.Parameters.Add("@Guid", SqliteType.Blob).Value = Guid.NewGuid().ToByteArray();
        command.Parameters.AddWithValue("@Status", (int)ItemStatus.Updated);
        command.Parameters.AddWithValue("@LastModified", timestamp);
        command.Parameters.AddWithValue("@Type", "Movie");
        command.ExecuteNonQuery();
    }

    private int SnapshotItemCount(Guid checkpointId)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select COUNT(*) from checkpoint_items where CheckpointId = @CheckpointId;";
        command.Parameters.Add("@CheckpointId", SqliteType.Blob).Value = checkpointId.ToByteArray();
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private IReadOnlyList<long> ReadTimestamps(string table)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"select LastModified from {table} order by LastModified;";
        using var reader = command.ExecuteReader();
        var timestamps = new List<long>();
        while (reader.Read())
        {
            timestamps.Add(reader.GetInt64(0));
        }
        return timestamps;
    }

    private static ItemRec Item(long timestamp)
        => new()
        {
            Guid = Guid.NewGuid(),
            Type = "Movie",
            Status = ItemStatus.Updated,
            LastModified = timestamp
        };

    private static UserInfoRec UserInfo(long timestamp)
        => new()
        {
            Guid = Guid.NewGuid(),
            UserId = "user-1",
            Type = "Movie",
            LastModified = timestamp
        };
}
