using System;
using System.IO;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for CompilationCacheService - cache management for dynamic compilation.
/// </summary>
public class CompilationCacheServiceTest : IDisposable
{
    private readonly string _testCacheDir;
    private readonly CompilationCacheService _service;

    public CompilationCacheServiceTest()
    {
        // Create a unique test directory for each test run
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"mesh-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        var options = Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true,
            EnableSourceDebugging = true
        });

        _service = new CompilationCacheService(options, NullLogger<CompilationCacheService>.Instance);
    }

    public void Dispose()
    {
        // Cleanup test directory
        if (Directory.Exists(_testCacheDir))
        {
            try
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void CacheDirectory_ReturnsAbsolutePath()
    {
        // Act
        var cacheDir = _service.CacheDirectory;

        // Assert
        cacheDir.Should().Be(_testCacheDir);
        Path.IsPathRooted(cacheDir).Should().BeTrue();
    }

    [Fact]
    public void SanitizeNodeName_ReplacesInvalidCharacters()
    {
        // Arrange & Act
        var result = _service.SanitizeNodeName("graph/org/project");

        // Assert
        result.Should().Be("graph_org_project");
        result.Should().NotContain("/");
    }

    [Fact]
    public void SanitizeNodeName_HandlesSpecialCharacters()
    {
        // Arrange & Act
        var result = _service.SanitizeNodeName("path:with*special?chars");

        // Assert
        result.Should().NotContain(":");
        result.Should().NotContain("*");
        result.Should().NotContain("?");
    }

    [Fact]
    public void SanitizeNodeName_EnsuresValidIdentifier()
    {
        // Arrange & Act
        var result = _service.SanitizeNodeName("123-starts-with-number");

        // Assert
        result.Should().StartWith("Node_", "Identifiers must start with a letter");
    }

    [Fact]
    public void GetDllPath_ReturnsCorrectPath()
    {
        // Arrange
        var nodeName = "test_node";

        // Act
        var dllPath = _service.GetDllPath(nodeName);

        // Assert
        dllPath.Should().Be(Path.Combine(_testCacheDir, "test_node.dll"));
    }

    [Fact]
    public void GetPdbPath_ReturnsCorrectPath()
    {
        // Arrange
        var nodeName = "test_node";

        // Act
        var pdbPath = _service.GetPdbPath(nodeName);

        // Assert
        pdbPath.Should().Be(Path.Combine(_testCacheDir, "test_node.pdb"));
    }

    [Fact]
    public void GetSourcePath_ReturnsCorrectPath()
    {
        // Arrange
        var nodeName = "test_node";

        // Act
        var sourcePath = _service.GetSourcePath(nodeName);

        // Assert
        sourcePath.Should().Be(Path.Combine(_testCacheDir, "test_node.cs"));
    }

    [Fact]
    public void IsCacheValid_ReturnsFalse_WhenDllDoesNotExist()
    {
        // Arrange
        var nodeName = "nonexistent";
        var lastModified = DateTimeOffset.UtcNow;

        // Act
        var isValid = _service.IsCacheValid(nodeName, lastModified);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void IsCacheValid_ReturnsFalse_WhenPdbDoesNotExist()
    {
        // Arrange
        var nodeName = "dll_only";
        File.WriteAllText(_service.GetDllPath(nodeName), "dummy dll");
        var lastModified = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Act
        var isValid = _service.IsCacheValid(nodeName, lastModified);

        // Assert
        isValid.Should().BeFalse();
    }

    /// <summary>
    /// Lays out the timestamped-subdir cache that <see cref="CompilationCacheService.TryGetLatestCachedDllPath"/>
    /// expects: <c>{cacheDir}/{nodeName}_{ticks_hex}/{nodeName}.{dll,pdb}</c>
    /// plus the flat <c>{cacheDir}/{nodeName}.cs</c> source mirror. Mirrors what
    /// <see cref="MeshNodeCompilationService.CompileToDiskAsync"/> writes at runtime.
    /// </summary>
    private string CreateCacheArtifacts(string nodeName, DateTime? dllWriteTime = null)
    {
        var subdir = Path.Combine(_testCacheDir, $"{nodeName}_{DateTimeOffset.UtcNow.Ticks:x}");
        Directory.CreateDirectory(subdir);
        var dllPath = Path.Combine(subdir, $"{nodeName}.dll");
        var pdbPath = Path.Combine(subdir, $"{nodeName}.pdb");
        var sourcePath = _service.GetSourcePath(nodeName);
        File.WriteAllText(dllPath, "dummy dll");
        File.WriteAllText(pdbPath, "dummy pdb");
        File.WriteAllText(sourcePath, "dummy source");
        if (dllWriteTime is { } t)
            File.SetLastWriteTimeUtc(dllPath, t);
        return dllPath;
    }

    [Fact]
    public void IsCacheValid_ReturnsTrue_WhenCacheIsNewer()
    {
        // Arrange — write a subdir-style cache entry matching the runtime layout.
        var nodeName = "valid_cache";
        CreateCacheArtifacts(nodeName);

        // Node was modified before the cache was created
        var lastModified = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Act
        var isValid = _service.IsCacheValid(nodeName, lastModified);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void IsCacheValid_ReturnsFalse_WhenNodeIsNewer()
    {
        // Arrange — write a subdir-style cache entry with a backdated DLL.
        var nodeName = "stale_cache";
        CreateCacheArtifacts(nodeName, dllWriteTime: DateTime.UtcNow.AddHours(-1));

        // Node was modified after the cache was created
        var lastModified = DateTimeOffset.UtcNow;

        // Act
        var isValid = _service.IsCacheValid(nodeName, lastModified);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void InvalidateCache_FlipsValidityFalse()
    {
        // Post-NodeTypeReleaseRedesign: InvalidateCache no longer deletes cache
        // files (release DLLs are content-keyed and accumulate; deleting them
        // races still-mapped ALCs on Windows). Instead it flips a sticky
        // invalidation flag the next IsCacheValid honours, and unloads the ALC
        // for the affected NodeType. Verify the validity contract here; ALC
        // unload is exercised by NodeTypeService / CodeEditRecompileTest.
        var nodeName = "to_invalidate";
        // Write a subdir-style cache entry stamped forward of "now" so
        // IsCacheValid returns true before InvalidateCache flips the sticky flag.
        var dllPath = CreateCacheArtifacts(nodeName, dllWriteTime: DateTime.UtcNow.AddHours(1));
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        var sourcePath = _service.GetSourcePath(nodeName);
        var futureStamp = DateTime.UtcNow.AddHours(1);
        File.SetLastWriteTimeUtc(pdbPath, futureStamp);
        File.SetLastWriteTimeUtc(sourcePath, futureStamp);

        var anyTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        _service.IsCacheValid(nodeName, anyTime).Should().BeTrue(
            "all three artifacts exist and are newer than the lastModified");

        _service.InvalidateCache(nodeName);

        _service.IsCacheValid(nodeName, anyTime).Should().BeFalse(
            "InvalidateCache flips a sticky flag; the next IsCacheValid must return false " +
            "until a fresh compile + MarkCacheFresh clears it");
    }

    [Fact]
    public void InvalidateCache_DoesNotThrow_WhenFilesDoNotExist()
    {
        // Arrange
        var nodeName = "nonexistent_cache";

        // Act & Assert
        var act = () => _service.InvalidateCache(nodeName);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetAllCachedAssemblyPaths_ReturnsAllDlls()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testCacheDir, "node1.dll"), "dll1");
        File.WriteAllText(Path.Combine(_testCacheDir, "node2.dll"), "dll2");
        File.WriteAllText(Path.Combine(_testCacheDir, "node3.dll"), "dll3");
        File.WriteAllText(Path.Combine(_testCacheDir, "other.pdb"), "pdb"); // Should be ignored

        // Act
        var paths = _service.GetAllCachedAssemblyPaths().ToList();

        // Assert
        paths.Should().HaveCount(3);
        paths.Should().AllSatisfy(p => p.Should().EndWith(".dll"));
    }

    [Fact]
    public void GetAllCachedAssemblyPaths_ReturnsEmpty_WhenNoCacheExists()
    {
        // Arrange
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty-cache-{Guid.NewGuid():N}");
        var options = Options.Create(new CompilationCacheOptions { CacheDirectory = emptyDir });
        var service = new CompilationCacheService(options, NullLogger<CompilationCacheService>.Instance);

        // Act
        var paths = service.GetAllCachedAssemblyPaths();

        // Assert
        paths.Should().BeEmpty();
    }

    [Fact]
    public void EnsureCacheDirectoryExists_CreatesDirectory()
    {
        // Arrange
        var newDir = Path.Combine(Path.GetTempPath(), $"new-cache-{Guid.NewGuid():N}");
        var options = Options.Create(new CompilationCacheOptions { CacheDirectory = newDir });
        var service = new CompilationCacheService(options, NullLogger<CompilationCacheService>.Instance);

        try
        {
            // Act
            service.EnsureCacheDirectoryExists();

            // Assert
            Directory.Exists(newDir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(newDir))
                Directory.Delete(newDir);
        }
    }
}

/// <summary>
/// Tests for CompilationCacheService when caching is disabled.
/// </summary>
public class CompilationCacheServiceDisabledTest
{
    [Fact]
    public void IsCacheValid_ReturnsFalse_WhenCachingDisabled()
    {
        // Arrange
        var options = Options.Create(new CompilationCacheOptions
        {
            EnableCompilationCache = false
        });
        var service = new CompilationCacheService(options, NullLogger<CompilationCacheService>.Instance);

        // Act
        var isValid = service.IsCacheValid("any_node", DateTimeOffset.UtcNow);

        // Assert
        isValid.Should().BeFalse();
    }
}

/// <summary>
/// Tests for CompilationCacheService AssemblyLoadContext management.
/// </summary>
public class CompilationCacheServiceLoadContextTest : IDisposable
{
    private readonly string _testCacheDir;
    private readonly CompilationCacheService _service;

    public CompilationCacheServiceLoadContextTest()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"load-context-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        var options = Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true,
            EnableSourceDebugging = true
        });

        _service = new CompilationCacheService(options, NullLogger<CompilationCacheService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();

        if (Directory.Exists(_testCacheDir))
        {
            try
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void GetOrCreateLoadContext_CreatesNewContext()
    {
        // Arrange
        var nodeName = "test_context";

        // Act
        var context = _service.GetOrCreateLoadContext(nodeName);

        // Assert
        context.Should().NotBeNull();
        context.NodeName.Should().Be(nodeName);
        context.IsCollectible.Should().BeTrue("Context should be collectible for unloading");
        context.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void GetOrCreateLoadContext_ReturnsSameContextForSameNode()
    {
        // Arrange
        var nodeName = "same_context";

        // Act
        var context1 = _service.GetOrCreateLoadContext(nodeName);
        var context2 = _service.GetOrCreateLoadContext(nodeName);

        // Assert
        context1.Should().BeSameAs(context2, "Same context should be returned for same node");
    }

    [Fact]
    public void GetOrCreateLoadContext_ReturnsDifferentContextsForDifferentNodes()
    {
        // Arrange & Act
        var context1 = _service.GetOrCreateLoadContext("node1");
        var context2 = _service.GetOrCreateLoadContext("node2");

        // Assert
        context1.Should().NotBeSameAs(context2, "Different nodes should have different contexts");
        context1.NodeName.Should().Be("node1");
        context2.NodeName.Should().Be("node2");
    }

    [Fact]
    public void UnloadContext_DisposesAndRemovesContext()
    {
        // Arrange
        var nodeName = "to_unload";
        var context = _service.GetOrCreateLoadContext(nodeName);

        // Act
        _service.UnloadContext(nodeName);

        // Assert
        context.IsDisposed.Should().BeTrue("Context should be disposed after unload");

        // New context should be created after unload
        var newContext = _service.GetOrCreateLoadContext(nodeName);
        newContext.Should().NotBeSameAs(context, "New context should be created after unload");
    }

    [Fact]
    public void UnloadContext_DoesNotThrow_WhenContextDoesNotExist()
    {
        // Act
        var act = () => _service.UnloadContext("nonexistent");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void LoadAssembly_ReturnsNull_WhenDllDoesNotExist()
    {
        // Arrange
        var nodeName = "no_dll";

        // Act
        var assembly = _service.LoadAssembly(nodeName);

        // Assert
        assembly.Should().BeNull("No DLL exists for this node");
    }

    [Fact]
    public void InvalidateCache_UnloadsContext()
    {
        // Arrange
        var nodeName = "to_invalidate";
        var context = _service.GetOrCreateLoadContext(nodeName);

        // Act
        _service.InvalidateCache(nodeName);

        // Assert
        context.IsDisposed.Should().BeTrue("Context should be disposed when cache is invalidated");
    }

    [Fact]
    public void Dispose_UnloadsAllContexts()
    {
        // Arrange
        var context1 = _service.GetOrCreateLoadContext("node1");
        var context2 = _service.GetOrCreateLoadContext("node2");
        var context3 = _service.GetOrCreateLoadContext("node3");

        // Act
        _service.Dispose();

        // Assert
        context1.IsDisposed.Should().BeTrue();
        context2.IsDisposed.Should().BeTrue();
        context3.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void GetOrCreateLoadContext_ThrowsAfterDispose()
    {
        // Arrange
        _service.Dispose();

        // Act
        var act = () => { _service.GetOrCreateLoadContext("test"); };

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void LoadAssembly_ThrowsAfterDispose()
    {
        // Arrange
        _service.Dispose();

        // Act
        var act = () => { _service.LoadAssembly("test"); };

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }
}
