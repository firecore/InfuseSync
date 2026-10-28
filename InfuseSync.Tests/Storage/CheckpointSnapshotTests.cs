using InfuseSync.Models;
using InfuseSync.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfuseSync.Tests.Storage;

public sealed class CheckpointSnapshotTests : IDisposable
{
    private readonly string _databaseDirectory;
    private readonly Db _database;

    public CheckpointSnapshotTests()
    {
        _databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "InfuseSync.Tests",
            Guid.NewGuid().ToString("N"));
        _database = new Db(_databaseDirectory, NullLogger.Instance);
    }

    [Fact]
    public void Paging_DoesNotSkipItemWhenEarlierLiveRowLeavesInterval()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var ids = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000004")
        };

        _database.SaveItems(ids.Select((id, index) => Item(id, checkpoint.Timestamp + index + 1)));
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);

        var firstPage = _database.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 0, 2);

        // This update removes the first row from the live interval. Paging the live
        // table with OFFSET would now skip the third row because every later row shifts.
        _database.SaveItems(new[] { Item(ids[0], checkpoint.Timestamp + 20) });

        var secondPage = _database.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 2, 2);

        Assert.Equal(ids.Take(2), firstPage.Select(item => item.Guid));
        Assert.Equal(ids.Skip(2), secondPage.Select(item => item.Guid));
        Assert.Equal(ids, firstPage.Concat(secondPage).Select(item => item.Guid));
        Assert.Equal(4, _database.ItemsCount(checkpoint.Guid, ItemStatus.Updated, null));
    }

    [Fact]
    public void StartSync_IsIdempotentAndKeepsItsOriginalSnapshot()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var firstId = Guid.NewGuid();
        var laterId = Guid.NewGuid();

        _database.SaveItems(new[] { Item(firstId, checkpoint.Timestamp + 1) });
        var firstStart = _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);

        _database.SaveItems(new[] { Item(laterId, checkpoint.Timestamp + 11) });
        var retry = _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 20);

        Assert.Equal(firstStart.SyncTimestamp, retry.SyncTimestamp);
        Assert.Collection(
            _database.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 0, 10),
            item => Assert.Equal(firstId, item.Guid));
    }

    [Fact]
    public void StartSync_RetryRefreshesCheckpointActivity()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var firstStart = _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);
        SetLastActivity(checkpoint.Guid, 50);

        var retry = _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 20);

        Assert.Equal(firstStart.SyncTimestamp, retry.SyncTimestamp);
        Assert.True(retry.LastActivity > 50);
        Assert.Equal(retry.LastActivity, _database.GetCheckpoint(checkpoint.Guid).LastActivity);
    }

    [Fact]
    public void Snapshot_KeepsUserDataScopedToCheckpointUser()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var itemId = Guid.NewGuid();

        _database.SaveUserInfo(
            new List<UserInfoRec>
            {
                UserInfo(itemId, "user-1", checkpoint.Timestamp + 1),
                UserInfo(itemId, "user-2", checkpoint.Timestamp + 2)
            });
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);

        _database.SaveUserInfo(
            new List<UserInfoRec>
            {
                UserInfo(itemId, "user-1", checkpoint.Timestamp + 20)
            });

        Assert.Collection(
            _database.GetUserInfos(checkpoint.Guid, null, 0, 10),
            item =>
            {
                Assert.Equal("user-1", item.UserId);
                Assert.Equal(checkpoint.Timestamp + 1, item.LastModified);
            });
        Assert.Equal(1, _database.UserInfoCount(checkpoint.Guid, null));
    }

    [Fact]
    public void ReplacingCheckpoint_RemovesPreviousSnapshot()
    {
        var first = _database.CreateCheckpoint("living-room", "user-1");
        _database.SaveItems(new[] { Item(Guid.NewGuid(), first.Timestamp + 1) });
        _database.StartSync(first.Guid, first.Timestamp + 10);

        var replacement = _database.CreateCheckpoint("living-room", "user-1");

        Assert.Empty(_database.GetItems(first.Guid, ItemStatus.Updated, null, 0, 10));
        Assert.Equal(first.Timestamp + 10, replacement.Timestamp);
    }

    [Fact]
    public void VersionTwoDatabase_MigratesActiveCheckpointIntoSnapshot()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var itemId = Guid.NewGuid();
        _database.SaveItems(new[] { Item(itemId, checkpoint.Timestamp + 1) });
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);
        _database.Dispose();

        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Filename={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "drop table checkpoint_items; drop table checkpoint_user_info; PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        using var migrated = new Db(_databaseDirectory, NullLogger.Instance);

        Assert.Collection(
            migrated.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 0, 10),
            item => Assert.Equal(itemId, item.Guid));
    }

    [Fact]
    public void VersionThreeDatabase_AddsActivityWithoutLosingCheckpointOrSnapshot()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var itemId = Guid.NewGuid();
        _database.SaveItems(new[] { Item(itemId, checkpoint.Timestamp + 1) });
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);
        _database.Dispose();

        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Filename={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "alter table checkpoints rename to checkpoints_v4; " +
                "create table checkpoints (Guid GUID PRIMARY KEY, DeviceId TEXT NOT NULL, UserId TEXT NOT NULL, Timestamp INTEGER NOT NULL, SyncTimestamp INTEGER NULL); " +
                "insert into checkpoints(Guid, DeviceId, UserId, Timestamp, SyncTimestamp) select Guid, DeviceId, UserId, Timestamp, SyncTimestamp from checkpoints_v4; " +
                "drop table checkpoints_v4; " +
                "PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }

        var migrationStarted = DateTime.UtcNow.AddSeconds(-1).ToFileTime();
        using var migrated = new Db(_databaseDirectory, NullLogger.Instance);
        var migratedCheckpoint = migrated.GetCheckpoint(checkpoint.Guid);

        Assert.NotNull(migratedCheckpoint);
        Assert.True(migratedCheckpoint.LastActivity >= migrationStarted);
        Assert.Equal(checkpoint.Timestamp + 10, migratedCheckpoint.SyncTimestamp);
        Assert.Collection(
            migrated.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 0, 10),
            item => Assert.Equal(itemId, item.Guid));
        Assert.Equal(4, ReadUserVersion());
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_databaseDirectory, true);
    }

    private string DatabasePath => Path.Combine(_databaseDirectory, "infuse_sync.db");

    private void SetLastActivity(Guid checkpointId, long lastActivity)
    {
        using var connection = new SqliteConnection($"Filename={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "update checkpoints set LastActivity=@LastActivity where Guid=@Guid;";
        command.Parameters.AddWithValue("@LastActivity", lastActivity);
        command.Parameters.Add("@Guid", SqliteType.Blob).Value = checkpointId.ToByteArray();
        command.ExecuteNonQuery();
    }

    private int ReadUserVersion()
    {
        using var connection = new SqliteConnection($"Filename={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static ItemRec Item(Guid guid, long timestamp)
        => new()
        {
            Guid = guid,
            Type = "Movie",
            Status = ItemStatus.Updated,
            LastModified = timestamp
        };

    private static UserInfoRec UserInfo(Guid guid, string userId, long timestamp)
        => new()
        {
            Guid = guid,
            UserId = userId,
            Type = "Movie",
            LastModified = timestamp
        };
}
