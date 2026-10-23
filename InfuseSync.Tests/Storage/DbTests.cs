using InfuseSync.Models;
using InfuseSync.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfuseSync.Tests.Storage;

public sealed class DbTests : IDisposable
{
    private readonly string _databaseDirectory;
    private readonly TestDb _database;

    public DbTests()
    {
        _databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "InfuseSync.Tests",
            Guid.NewGuid().ToString("N"));
        _database = new TestDb(_databaseDirectory, () => 1000);
    }

    [Fact]
    public void CreateCheckpoint_ReplacesPreviousCheckpointAndCarriesForwardCursor()
    {
        var first = _database.CreateCheckpoint("living-room", "user-1");
        var syncTimestamp = first.Timestamp + 42;
        _database.StartSync(first.Guid, syncTimestamp);

        var replacement = _database.CreateCheckpoint("living-room", "user-1");

        Assert.Null(_database.GetCheckpoint(first.Guid));
        Assert.Equal(syncTimestamp, replacement.Timestamp);
        Assert.Null(replacement.SyncTimestamp);
        Assert.Equal(replacement.Guid, _database.GetCheckpoint(replacement.Guid).Guid);
    }

    [Fact]
    public void SaveItems_RoundTripsStatusAndTypeFilters()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var movieId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var removedId = Guid.NewGuid();

        _database.SaveItems(
            new[]
            {
                Item(movieId, "Movie", ItemStatus.Updated, checkpoint.Timestamp + 1),
                Item(episodeId, "Episode", ItemStatus.Updated, checkpoint.Timestamp + 2),
                Item(removedId, "Movie", ItemStatus.Removed, checkpoint.Timestamp + 3)
            });
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);

        var movies = _database.GetItems(
            checkpoint.Guid,
            ItemStatus.Updated,
            new[] { "Movie" },
            0,
            10);
        var removed = _database.GetItems(
            checkpoint.Guid,
            ItemStatus.Removed,
            null,
            0,
            10);

        Assert.Collection(movies, item => Assert.Equal(movieId, item.Guid));
        Assert.Collection(removed, item => Assert.Equal(removedId, item.Guid));
        Assert.Equal(2, _database.ItemsCount(checkpoint.Guid, ItemStatus.Updated, null));
    }

    [Fact]
    public void SaveUserInfo_KeepsUsersIndependent()
    {
        var firstCheckpoint = _database.CreateCheckpoint("living-room", "user-1");
        var secondCheckpoint = _database.CreateCheckpoint("living-room", "user-2");
        var itemId = Guid.NewGuid();
        _database.SaveUserInfo(
            new List<UserInfoRec>
            {
                UserInfo(itemId, "user-1", firstCheckpoint.Timestamp + 1),
                UserInfo(itemId, "user-2", secondCheckpoint.Timestamp + 2)
            });

        _database.SaveUserInfo(
            new List<UserInfoRec> { UserInfo(itemId, "user-1", firstCheckpoint.Timestamp + 5) });
        _database.StartSync(firstCheckpoint.Guid, firstCheckpoint.Timestamp + 10);
        _database.StartSync(secondCheckpoint.Guid, secondCheckpoint.Timestamp + 10);

        var firstUser = _database.GetUserInfos(firstCheckpoint.Guid, null, 0, 10);
        var secondUser = _database.GetUserInfos(secondCheckpoint.Guid, null, 0, 10);

        Assert.Collection(firstUser, item => Assert.Equal(firstCheckpoint.Timestamp + 5, item.LastModified));
        Assert.Collection(secondUser, item => Assert.Equal(secondCheckpoint.Timestamp + 2, item.LastModified));
        Assert.Equal(1, _database.UserInfoCount(firstCheckpoint.Guid, null));
        Assert.Equal(1, _database.UserInfoCount(secondCheckpoint.Guid, null));
    }

    [Fact]
    public void GetItems_OrdersPagesByTimestampAndGuid()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var thirdId = Guid.Parse("00000000-0000-0000-0000-000000000003");

        _database.SaveItems(
            new[]
            {
                Item(thirdId, "Movie", ItemStatus.Updated, checkpoint.Timestamp + 2),
                Item(secondId, "Movie", ItemStatus.Updated, checkpoint.Timestamp + 1),
                Item(firstId, "Movie", ItemStatus.Updated, checkpoint.Timestamp + 1)
            });
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);

        var firstPage = _database.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 0, 2);
        var secondPage = _database.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 2, 2);

        Assert.Equal(new[] { firstId, secondId }, firstPage.Select(item => item.Guid));
        Assert.Collection(secondPage, item => Assert.Equal(thirdId, item.Guid));
    }

    [Fact]
    public void GetUserInfos_OrdersPagesByTimestampAndGuid()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var thirdId = Guid.Parse("00000000-0000-0000-0000-000000000003");

        _database.SaveUserInfo(
            new List<UserInfoRec>
            {
                UserInfo(thirdId, "user-1", checkpoint.Timestamp + 2),
                UserInfo(secondId, "user-1", checkpoint.Timestamp + 1),
                UserInfo(firstId, "user-1", checkpoint.Timestamp + 1)
            });
        _database.StartSync(checkpoint.Guid, checkpoint.Timestamp + 10);

        var firstPage = _database.GetUserInfos(checkpoint.Guid, null, 0, 2);
        var secondPage = _database.GetUserInfos(checkpoint.Guid, null, 2, 2);

        Assert.Equal(new[] { firstId, secondId }, firstPage.Select(item => item.Guid));
        Assert.Collection(secondPage, item => Assert.Equal(thirdId, item.Guid));
    }

    [Fact]
    public async Task WatermarkOperationsAreSerializedByTheWriteLock()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var items = new[]
        {
            Item(Guid.NewGuid(), "Movie", ItemStatus.Updated, 0),
            Item(Guid.NewGuid(), "Episode", ItemStatus.Updated, 0)
        };
        var userInfo = new[]
        {
            UserInfo(Guid.NewGuid(), "user-1", 0),
            UserInfo(Guid.NewGuid(), "user-1", 0)
        };
        var heldLock = _database.HoldWriteLock();
        Task saveItems = null;
        Task saveUserInfo = null;
        Task<Checkpoint> startSync = null;

        try
        {
            saveItems = Task.Run(() => _database.SaveItemsNow(items));
            saveUserInfo = Task.Run(() => _database.SaveUserInfoNow(userInfo));
            startSync = Task.Run(() => _database.StartSync(checkpoint.Guid));

            Assert.True(SpinWait.SpinUntil(() => _database.WaitingWriters == 3, TimeSpan.FromSeconds(5)));
            Assert.All(items, item => Assert.Equal(0, item.LastModified));
            Assert.All(userInfo, info => Assert.Equal(0, info.LastModified));
        }
        finally
        {
            heldLock.Dispose();
            if (saveItems != null && saveUserInfo != null && startSync != null)
            {
                await Task.WhenAll(saveItems, saveUserInfo, startSync);
            }
        }

        Assert.Equal(items[0].LastModified, items[1].LastModified);
        Assert.Equal(userInfo[0].LastModified, userInfo[1].LastModified);
        var watermarks = new[]
        {
            items[0].LastModified,
            userInfo[0].LastModified,
            (await startSync).SyncTimestamp.Value
        }.OrderBy(value => value).ToArray();
        Assert.Equal(new long[] { 1001, 1002, 1003 }, watermarks);
    }

    [Fact]
    public void FailedWriteRollsBackItsWatermark()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var invalid = Item(Guid.NewGuid(), null, ItemStatus.Updated, 0);
        Assert.ThrowsAny<Exception>(() => _database.SaveItemsNow(new[] { invalid }));

        var valid = Item(Guid.NewGuid(), "Movie", ItemStatus.Updated, 0);
        _database.SaveItemsNow(new[] { valid });
        _database.StartSync(checkpoint.Guid);

        Assert.Equal(1001, invalid.LastModified);
        Assert.Equal(1001, valid.LastModified);
        Assert.Equal(1, _database.ItemsCount(checkpoint.Guid, ItemStatus.Updated, null));
    }

    [Fact]
    public void WatermarkSurvivesClockChangesAcrossAllOperations()
    {
        var clock = new MutableClock(1000);
        var path = Path.Combine(_databaseDirectory, "clock-changes");
        using var database = new TestDb(path, () => clock.Value);

        var checkpoint = database.CreateCheckpoint("living-room", "user-1");
        clock.Value = 900;
        var item = Item(Guid.NewGuid(), "Movie", ItemStatus.Updated, 0);
        database.SaveItemsNow(new[] { item });
        clock.Value = 5000;
        var syncTimestamp = database.StartSync(checkpoint.Guid).SyncTimestamp.Value;
        clock.Value = 4000;
        var userInfo = UserInfo(Guid.NewGuid(), "user-1", 0);
        database.SaveUserInfoNow(new[] { userInfo });

        Assert.Equal(1000, checkpoint.Timestamp);
        Assert.Equal(1001, item.LastModified);
        Assert.Equal(5000, syncTimestamp);
        Assert.Equal(5001, userInfo.LastModified);
    }

    [Fact]
    public void AutomaticStartSyncCreatesAnIdempotentSnapshotAtTheMonotonicCursor()
    {
        var checkpoint = _database.CreateCheckpoint("living-room", "user-1");
        var included = Item(Guid.NewGuid(), "Movie", ItemStatus.Updated, 0);
        _database.SaveItemsNow(new[] { included });

        var started = _database.StartSync(checkpoint.Guid);

        var excluded = Item(Guid.NewGuid(), "Movie", ItemStatus.Updated, 0);
        _database.SaveItemsNow(new[] { excluded });
        var retry = _database.StartSync(checkpoint.Guid);
        var snapshot = _database.GetItems(checkpoint.Guid, ItemStatus.Updated, null, 0, 10);

        Assert.Equal(1000, checkpoint.Timestamp);
        Assert.Equal(1001, included.LastModified);
        Assert.Equal(1002, started.SyncTimestamp);
        Assert.Equal(started.SyncTimestamp, retry.SyncTimestamp);
        Assert.Collection(snapshot, item => Assert.Equal(included.Guid, item.Guid));
        Assert.DoesNotContain(snapshot, item => item.Guid == excluded.Guid);
    }

    [Fact]
    public void WatermarkPersistsAndCanBeRebuiltFromExistingRows()
    {
        var path = Path.Combine(_databaseDirectory, "restart");
        var item = Item(Guid.NewGuid(), "Movie", ItemStatus.Updated, 0);
        using (var database = new TestDb(path, () => 1000))
        {
            database.SaveItemsNow(new[] { item });
        }

        var databasePath = Path.Combine(path, "infuse_sync.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select Value from sync_watermark where Id=1;";
            Assert.Equal(1000L, (long)command.ExecuteScalar());
            command.CommandText = "drop table sync_watermark;";
            command.ExecuteNonQuery();
        }

        var userInfo = UserInfo(Guid.NewGuid(), "user-1", 0);
        using (var database = new TestDb(path, () => 10))
        {
            database.SaveUserInfoNow(new[] { userInfo });
        }

        Assert.Equal(1001, userInfo.LastModified);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_databaseDirectory, true);
    }

    private static ItemRec Item(Guid guid, string type, ItemStatus status, long timestamp)
        => new()
        {
            Guid = guid,
            Type = type,
            Status = status,
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

    private sealed class TestDb : Db
    {
        public TestDb(string path, Func<long> utcFileTime)
            : base(path, NullLogger.Instance, utcFileTime)
        {
        }

        public int WaitingWriters => WriteLock.WaitingWriteCount;

        public IDisposable HoldWriteLock()
        {
            return WriteLock.Write();
        }
    }

    private sealed class MutableClock
    {
        public MutableClock(long value)
        {
            Value = value;
        }

        public long Value { get; set; }
    }
}
