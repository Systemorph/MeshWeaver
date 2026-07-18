using Memex.Portal.Shared;
using MeshWeaver.ContentCollections;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the fail-fast guard (<see cref="MemexConfiguration.ValidateContentStorageDurability"/>) that turns
/// the silent content-data-loss footgun of issue #435 into a loud startup failure: a DEPLOYED
/// (non-development) <c>FileSystem</c> content store with an empty or relative <c>BasePath</c> resolves
/// against the container's ephemeral working directory and loses every uploaded file on teardown. An
/// ABSOLUTE path is accepted (code cannot verify a mount is durable, only reject the guaranteed-loss
/// case); a local Development run keeps the relative-to-working-tree convenience.
/// </summary>
public class ContentStorageDurabilityTest
{
    private static ContentCollectionConfig FileSystem(string? basePath) =>
        new() { Name = "storage", SourceType = "FileSystem", BasePath = basePath };

    [Fact]
    public void NonDev_FileSystem_EmptyBasePath_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MemexConfiguration.ValidateContentStorageDurability(FileSystem(""), isDevelopment: false));
        ex.Message.Should().Contain("issue #435");
        ex.Message.Should().Contain("empty");
        ex.Message.Should().Contain("durable");
    }

    [Fact]
    public void NonDev_FileSystem_NullBasePath_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MemexConfiguration.ValidateContentStorageDurability(FileSystem(null), isDevelopment: false));
        ex.Message.Should().Contain("empty");
    }

    [Fact]
    public void NonDev_FileSystem_RelativeBasePath_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MemexConfiguration.ValidateContentStorageDurability(
                FileSystem("../../samples/Graph"), isDevelopment: false));
        ex.Message.Should().Contain("relative");
        ex.Message.Should().Contain("../../samples/Graph");
    }

    [Fact]
    public void NonDev_FileSystem_AbsoluteBasePath_Accepted()
    {
        // Path.GetTempPath() is an absolute, rooted path on every OS the tests run on.
        var absolute = Path.GetTempPath();
        // Does not throw — an absolute path is the operator's chosen (presumed durable) mount.
        MemexConfiguration.ValidateContentStorageDurability(FileSystem(absolute), isDevelopment: false);
    }

    [Fact]
    public void NonDev_AzureBlob_EmptyBasePath_Accepted()
    {
        // AzureBlob does not root files on a local path — BasePath is a blob prefix, not a mount.
        var azure = new ContentCollectionConfig { Name = "storage", SourceType = "AzureBlob", BasePath = null };
        MemexConfiguration.ValidateContentStorageDurability(azure, isDevelopment: false);
    }

    [Fact]
    public void Development_FileSystem_RelativeBasePath_Accepted()
    {
        // Local dev resolves a relative path against a stable working tree — the Monolith's
        // Storage:BasePath = "../../samples/Graph". Must NOT fail fast.
        MemexConfiguration.ValidateContentStorageDurability(
            FileSystem("../../samples/Graph"), isDevelopment: true);
    }

    [Fact]
    public void NullConfig_Accepted()
    {
        MemexConfiguration.ValidateContentStorageDurability(contentStorageConfig: null, isDevelopment: false);
    }
}
