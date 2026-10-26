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
        _database.UpdateCheckpoint(first.Guid, 42);

        var replacement = _database.CreateCheckpoint("living-room", "user-1");

        Assert.Null(_database.GetCheckpoint(first.Guid));
        Assert.Equal(42, replacement.Timestamp);
        Assert.Null(replacement.SyncTimestamp);
        Assert.Equal(replacement.Guid, _database.GetCheckpoint(replacement.Guid).Guid);
    }

    [Fact]
    public void SaveItems_RoundTripsStatusAndTypeFilters()
    {
        var movieId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var removedId = Guid.NewGuid();

        _database.SaveItems(
            new[]
            {
                Item(movieId, "Movie", ItemStatus.Updated, 100),
                Item(episodeId, "Episode", ItemStatus.Updated, 101),
                Item(removedId, "Movie", ItemStatus.Removed, 102)
            });

        var movies = _database.GetItems(
            90,
            110,
            ItemStatus.Updated,
            new[] { "Movie" },
            0,
            10);
        var removed = _database.GetItems(
            90,
            110,
            ItemStatus.Removed,
            null,
            0,
            10);

        Assert.Collection(movies, item => Assert.Equal(movieId, item.Guid));
        Assert.Collection(removed, item => Assert.Equal(removedId, item.Guid));
        Assert.Equal(2, _database.ItemsCount(90, 110, ItemStatus.Updated, null));
    }

    [Fact]
    public void SaveUserInfo_KeepsUsersIndependent()
    {
        var itemId = Guid.NewGuid();
        _database.SaveUserInfo(
            new List<UserInfoRec>
            {
                UserInfo(itemId, "user-1", 100),
                UserInfo(itemId, "user-2", 101)
            });

        _database.SaveUserInfo(new List<UserInfoRec> { UserInfo(itemId, "user-1", 105) });

        var firstUser = _database.GetUserInfos(90, 110, "user-1", null, 0, 10);
        var secondUser = _database.GetUserInfos(90, 110, "user-2", null, 0, 10);

        Assert.Collection(firstUser, item => Assert.Equal(105, item.LastModified));
        Assert.Collection(secondUser, item => Assert.Equal(101, item.LastModified));
        Assert.Equal(1, _database.UserInfoCount(90, 110, "user-1", null));
        Assert.Equal(1, _database.UserInfoCount(90, 110, "user-2", null));
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
