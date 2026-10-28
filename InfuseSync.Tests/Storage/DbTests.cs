using InfuseSync.Models;
using InfuseSync.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfuseSync.Tests.Storage;

public sealed class DbTests : IDisposable
{
    private readonly string _databaseDirectory;
    private readonly Db _database;

    public DbTests()
    {
        _databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "InfuseSync.Tests",
            Guid.NewGuid().ToString("N"));
        _database = new Db(_databaseDirectory, NullLogger.Instance);
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
}
