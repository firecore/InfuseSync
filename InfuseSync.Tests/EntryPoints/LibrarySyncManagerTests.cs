using InfuseSync.EntryPoints;
using Xunit;

namespace InfuseSync.Tests.EntryPoints;

public sealed class LibrarySyncManagerTests
{
    [Theory]
    [InlineData("Folder", true)]
    [InlineData("Series", false)]
    [InlineData("Season", false)]
    [InlineData("BoxSet", false)]
    [InlineData("Playlist", false)]
    [InlineData(null, false)]
    public void LibraryRefresh_IsLimitedToProtocolFolders(string clientType, bool expected)
    {
        Assert.Equal(expected, LibrarySyncManager.ShouldRefreshAffectedLibraries(clientType));
    }

    [Fact]
    public void RemovedLibraryRoot_MatchesItsVirtualFolderLocation()
    {
        var paths = LibrarySyncManager.GetAffectedFolderPaths(
            "/media/movies",
            null,
            Array.Empty<string>());

        Assert.True(LibrarySyncManager.HasMatchingLocation(paths, new[] { "/media/movies" }));
    }

    [Fact]
    public void RemovedNestedFolder_MatchesAnAncestorLibraryLocation()
    {
        var paths = LibrarySyncManager.GetAffectedFolderPaths(
            "/media/movies/action",
            "/media/movies",
            new[] { "/media" });

        Assert.True(LibrarySyncManager.HasMatchingLocation(paths, new[] { "/media/movies" }));
        Assert.False(LibrarySyncManager.HasMatchingLocation(paths, new[] { "/media/shows" }));
    }

    [Fact]
    public void RemovedNestedFolder_MatchesLocationWithoutParentInformation()
    {
        var paths = LibrarySyncManager.GetAffectedFolderPaths(
            "/media/movies/action",
            null,
            Array.Empty<string>());

        Assert.True(LibrarySyncManager.HasMatchingLocation(paths, new[] { "/media/movies/" }));
        Assert.False(LibrarySyncManager.HasMatchingLocation(paths, new[] { "/media/movie" }));
    }

    [Fact]
    public void FolderPathMatching_IgnoresMissingAndDuplicatePaths()
    {
        var paths = LibrarySyncManager.GetAffectedFolderPaths(
            "/media/movies",
            "/media/movies",
            new string[] { null, string.Empty });

        Assert.Collection(paths, path => Assert.Equal("/media/movies", path));
        Assert.False(LibrarySyncManager.HasMatchingLocation(paths, null));
    }
}
