using InfuseSync.EntryPoints;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace InfuseSync.Tests.EntryPoints;

public sealed class SharedTests
{
    [Fact]
    public void ResolveUpdatedItem_UsesMatchingOwnerAndPrimaryBeforeLocalRelationshipIsStored()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var alternate = LocalAlternate(primary.Id);

        var result = Shared.ResolveUpdatedItem(
            alternate,
            id =>
            {
                Assert.Equal(primary.Id, id);
                return primary;
            },
            (_, _) => throw new InvalidOperationException("The ownership check should avoid a relationship lookup."));

        Assert.Same(primary, result);
    }

    [Fact]
    public void ResolveUpdatedItem_UsesOwnerAsFallbackWhenPrimaryVersionIsMissing()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var alternate = new Movie
        {
            Id = Guid.NewGuid(),
            OwnerId = primary.Id
        };

        var result = Shared.ResolveUpdatedItem(
            alternate,
            id =>
            {
                Assert.Equal(primary.Id, id);
                return primary;
            },
            (resolvedPrimary, versionId) =>
            {
                Assert.Same(primary, resolvedPrimary);
                Assert.Equal(alternate.Id, versionId);
                return true;
            });

        Assert.Same(primary, result);
    }

    [Fact]
    public void ResolveUpdatedItem_UsesRecordedLocalRelationshipAfterMigration()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var alternate = new Movie
        {
            Id = Guid.NewGuid(),
            PrimaryVersionId = primary.Id
        };

        var result = Shared.ResolveUpdatedItem(
            alternate,
            _ => primary,
            (resolvedPrimary, versionId) =>
            {
                Assert.Same(primary, resolvedPrimary);
                Assert.Equal(alternate.Id, versionId);
                return true;
            });

        Assert.Same(primary, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsLinkedAlternate()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var alternate = new Movie
        {
            Id = Guid.NewGuid(),
            PrimaryVersionId = primary.Id
        };

        var result = Shared.ResolveUpdatedItem(alternate, _ => primary, (_, _) => false);

        Assert.Same(alternate, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsExtra()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var extra = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = primary.Id,
            ExtraType = ExtraType.Trailer
        };

        var result = Shared.ResolveUpdatedItem(extra, _ => primary, (_, _) => false);

        Assert.Same(extra, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsAdditionalPart()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var additionalPart = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = primary.Id
        };

        var result = Shared.ResolveUpdatedItem(additionalPart, _ => primary, (_, _) => false);

        Assert.Same(additionalPart, result);
    }

    [Fact]
    public void ResolveUpdatedItem_FallsBackWhenPrimaryIsMissing()
    {
        var alternate = LocalAlternate(Guid.NewGuid());

        var result = Shared.ResolveUpdatedItem(
            alternate,
            _ => null,
            (_, _) => throw new InvalidOperationException("A missing primary has no relationship to inspect."));

        Assert.Same(alternate, result);
    }

    private static Movie LocalAlternate(Guid primaryId)
        => new()
        {
            Id = Guid.NewGuid(),
            OwnerId = primaryId,
            PrimaryVersionId = primaryId
        };
}
