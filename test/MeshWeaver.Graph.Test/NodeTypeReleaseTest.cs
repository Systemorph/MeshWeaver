using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for NodeTypeRelease - the unified compilation cache input and metadata class.
/// </summary>
public class NodeTypeReleaseTest
{
    [Fact]
    public void Create_SetsPathCorrectly()
    {
        // Arrange & Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            "public record Organization { }",
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Assert
        release.Path.Should().StartWith("Type/Organization@");
        release.Path.Should().Contain("@");
        release.NodeTypePath.Should().Be("Type/Organization");
    }

    [Fact]
    public void Create_SetsReleaseHash()
    {
        // Arrange & Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            "public record Organization { }",
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Assert
        release.Release.Should().NotBeNullOrEmpty();
        release.Release.Should().HaveLength(16, "Release hash should be 16 characters");
        release.Path.Should().EndWith(release.Release);
    }

    [Fact]
    public void Create_StoresCode()
    {
        // Arrange
        var code = "public record Organization { public string Name { get; init; } }";

        // Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            code,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Assert
        release.Code.Should().Be(code);
    }

    [Fact]
    public void Create_StoresHubConfiguration()
    {
        // Arrange
        var hubConfig = "config => config.AddData(d => d.AddSource(s => s.WithType<Organization>()))";

        // Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            hubConfig,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Assert
        release.HubConfiguration.Should().Be(hubConfig);
    }

    [Fact]
    public void Create_StoresContentCollections()
    {
        // Arrange
        var collections = new List<ContentCollectionConfig>
        {
            new() { Name = "docs", SourceType = "FileSystem", BasePath = "/data/docs" },
            new() { Name = "assets", SourceType = "FileSystem", BasePath = "/data/assets" }
        };

        // Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            null,
            collections,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Assert
        release.ContentCollections.Should().HaveCount(2);
        release.ContentCollections.Should().Contain(c => c.Name == "docs");
        release.ContentCollections.Should().Contain(c => c.Name == "assets");
    }

    [Fact]
    public void Create_SetsCreatedAt()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        var after = DateTimeOffset.UtcNow;

        // Assert
        release.CreatedAt.Should().BeOnOrAfter(before);
        release.CreatedAt.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Create_SetsFrameworkVersion()
    {
        // Arrange & Act
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "2.5.0");

        // Assert
        release.FrameworkVersion.Should().Be("2.5.0");
    }

    [Fact]
    public void ComputeRelease_IsDeterministic()
    {
        // Arrange
        var timestamp = DateTimeOffset.Parse("2024-01-15T10:00:00Z");

        // Act
        var release1 = NodeTypeRelease.ComputeRelease(
            "public record Org { }",
            "config => config",
            null,
            timestamp);

        var release2 = NodeTypeRelease.ComputeRelease(
            "public record Org { }",
            "config => config",
            null,
            timestamp);

        // Assert
        release1.Should().Be(release2, "Same inputs should produce same hash");
    }

    [Fact]
    public void ComputeRelease_DifferentCodeProducesDifferentHash()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var release1 = NodeTypeRelease.ComputeRelease(
            "public record Org1 { }",
            null,
            null,
            timestamp);

        var release2 = NodeTypeRelease.ComputeRelease(
            "public record Org2 { }",
            null,
            null,
            timestamp);

        // Assert
        release1.Should().NotBe(release2, "Different code should produce different hash");
    }

    [Fact]
    public void ComputeRelease_DifferentHubConfigProducesDifferentHash()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var release1 = NodeTypeRelease.ComputeRelease(
            null,
            "config => config.AddData()",
            null,
            timestamp);

        var release2 = NodeTypeRelease.ComputeRelease(
            null,
            "config => config.AddViews()",
            null,
            timestamp);

        // Assert
        release1.Should().NotBe(release2, "Different hub config should produce different hash");
    }

    [Fact]
    public void ComputeRelease_DifferentTimestampProducesDifferentHash()
    {
        // Arrange & Act
        var release1 = NodeTypeRelease.ComputeRelease(
            "public record Org { }",
            null,
            null,
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"));

        var release2 = NodeTypeRelease.ComputeRelease(
            "public record Org { }",
            null,
            null,
            DateTimeOffset.Parse("2024-01-02T00:00:00Z"));

        // Assert
        release1.Should().NotBe(release2, "Different framework timestamp should produce different hash");
    }

    [Fact]
    public void ComputeRelease_ContentCollectionsOrderDoesNotMatter()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;
        var collections1 = new List<ContentCollectionConfig>
        {
            new() { Name = "aaa", SourceType = "FileSystem" },
            new() { Name = "bbb", SourceType = "FileSystem" }
        };
        var collections2 = new List<ContentCollectionConfig>
        {
            new() { Name = "bbb", SourceType = "FileSystem" },
            new() { Name = "aaa", SourceType = "FileSystem" }
        };

        // Act
        var release1 = NodeTypeRelease.ComputeRelease(null, null, collections1, timestamp);
        var release2 = NodeTypeRelease.ComputeRelease(null, null, collections2, timestamp);

        // Assert
        release1.Should().Be(release2, "Collections are sorted by name, so order shouldn't matter");
    }

    [Fact]
    public void ComputeRelease_NullsAreHandled()
    {
        // Arrange & Act
        var release = NodeTypeRelease.ComputeRelease(null, null, null, DateTimeOffset.UtcNow);

        // Assert
        release.Should().NotBeNullOrEmpty();
        release.Should().HaveLength(16);
    }

    [Fact]
    public void ComputeRelease_IsUrlSafe()
    {
        // Arrange & Act
        var release = NodeTypeRelease.ComputeRelease(
            "public record Org { public string LongPropertyName { get; init; } }",
            "config => config.AddData(d => d.AddSource(s => s.WithType<Org>()))",
            null,
            DateTimeOffset.UtcNow);

        // Assert
        release.Should().NotContain("+", "URL-safe base64 replaces + with -");
        release.Should().NotContain("/", "URL-safe base64 replaces / with _");
        release.Should().NotContain("=", "Padding should be trimmed");
    }

    [Fact]
    public void GetSanitizedPath_ReplacesSlashes()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var sanitized = release.GetSanitizedPath();

        // Assert
        sanitized.Should().NotContain("/");
        sanitized.Should().StartWith("Type_Organization_");
    }

    [Fact]
    public void GetSanitizedPath_ReplacesAtSymbol()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var sanitized = release.GetSanitizedPath();

        // Assert
        sanitized.Should().NotContain("@");
    }

    [Fact]
    public void GetSanitizedPath_RemovesInvalidFileSystemCharacters()
    {
        // Arrange - Create release with path that would have special chars
        var release = new NodeTypeRelease
        {
            Path = "Type/Org<Test>@abc123",
            NodeTypePath = "Type/Org<Test>",
            Release = "abc123",
            FrameworkVersion = "1.0.0",
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var sanitized = release.GetSanitizedPath();

        // Assert
        sanitized.Should().NotContain("<");
        sanitized.Should().NotContain(">");
        sanitized.Should().NotContain("/");
        sanitized.Should().NotContain("@");
    }

    [Fact]
    public void GetSanitizedPath_CollapsesConsecutiveUnderscores()
    {
        // Arrange
        var release = new NodeTypeRelease
        {
            Path = "Type//Multiple///Slashes@abc123",
            NodeTypePath = "Type//Multiple///Slashes",
            Release = "abc123",
            FrameworkVersion = "1.0.0",
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var sanitized = release.GetSanitizedPath();

        // Assert
        sanitized.Should().NotContain("__");
    }

    [Fact]
    public void GetSanitizedPath_TrimsLeadingAndTrailingUnderscores()
    {
        // Arrange
        var release = new NodeTypeRelease
        {
            Path = "/Type/Org/@abc123",
            NodeTypePath = "/Type/Org/",
            Release = "abc123",
            FrameworkVersion = "1.0.0",
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var sanitized = release.GetSanitizedPath();

        // Assert
        sanitized.Should().NotStartWith("_");
        sanitized.Should().NotEndWith("_");
    }

    [Fact]
    public void GetSanitizedPath_IsSuitableForFileName()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "ACME/Type/Project",
            "public record Project { }",
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var sanitized = release.GetSanitizedPath();

        // Assert - Should be a valid file name
        var invalidChars = Path.GetInvalidFileNameChars();
        sanitized.Should().NotContainAny(invalidChars.Select(c => c.ToString()).ToArray());
    }
}

/// <summary>
/// Tests for CreateNodeTypeReleaseRequest and CreateNodeTypeReleaseResponse messages.
/// </summary>
public class NodeTypeReleaseMessagesTest
{
    [Fact]
    public void CreateNodeTypeReleaseRequest_StoresNodeTypePath()
    {
        // Arrange & Act
        var request = new CreateNodeTypeReleaseRequest("Type/Organization");

        // Assert
        request.NodeTypePath.Should().Be("Type/Organization");
    }

    [Fact]
    public void CreateNodeTypeReleaseRequest_StoresVersion()
    {
        // Arrange & Act
        var request = new CreateNodeTypeReleaseRequest("Type/Organization", "v1.0.0");

        // Assert
        request.Version.Should().Be("v1.0.0");
    }

    [Fact]
    public void CreateNodeTypeReleaseRequest_VersionIsOptional()
    {
        // Arrange & Act
        var request = new CreateNodeTypeReleaseRequest("Type/Organization");

        // Assert
        request.Version.Should().BeNull();
    }

    [Fact]
    public void CreateNodeTypeReleaseResponse_StoresRelease()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var response = new CreateNodeTypeReleaseResponse(release, "/path/to/assembly.dll");

        // Assert
        response.Release.Should().NotBeNull();
        response.Release!.NodeTypePath.Should().Be("Type/Organization");
        response.AssemblyPath.Should().Be("/path/to/assembly.dll");
        response.Error.Should().BeNull();
    }

    [Fact]
    public void CreateNodeTypeReleaseResponse_StoresError()
    {
        // Arrange & Act
        var response = new CreateNodeTypeReleaseResponse(null, Error: "Compilation failed");

        // Assert
        response.Release.Should().BeNull();
        response.AssemblyPath.Should().BeNull();
        response.Error.Should().Be("Compilation failed");
    }

    [Fact]
    public void CreateNodeTypeReleaseResponse_SuccessCase()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Person",
            "public record Person { public string Name { get; init; } }",
            "config => config.AddData()",
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var response = new CreateNodeTypeReleaseResponse(release, "/cache/Type_Person_abc123.dll");

        // Assert
        response.Release.Should().NotBeNull();
        response.Release!.Code.Should().Contain("public record Person");
        response.AssemblyPath.Should().EndWith(".dll");
        response.Error.Should().BeNull();
    }
}

/// <summary>
/// Integration tests for NodeTypeRelease with CompilationCacheService.
/// </summary>
public class NodeTypeReleaseIntegrationTest : IDisposable
{
    private readonly string _testCacheDir;
    private readonly CompilationCacheService _cacheService;

    public NodeTypeReleaseIntegrationTest()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"release-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        var options = Microsoft.Extensions.Options.Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true
        });

        _cacheService = new CompilationCacheService(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CompilationCacheService>.Instance);
    }

    public void Dispose()
    {
        _cacheService.Dispose();

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
    public void GetReleaseFolderPath_UsesNodeTypeRelease()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Organization",
            "public record Organization { }",
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var folderPath = _cacheService.GetReleaseFolderPath(release);

        // Assert
        folderPath.Should().StartWith(_testCacheDir);
        folderPath.Should().Contain("Type_Organization");
        Path.GetFileName(folderPath).Should().Be(release.GetSanitizedPath());
    }

    [Fact]
    public void GetReleaseFolderPath_DifferentReleasesGetDifferentFolders()
    {
        // Arrange
        var release1 = NodeTypeRelease.Create(
            "Type/Organization",
            "public record Organization1 { }",
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        var release2 = NodeTypeRelease.Create(
            "Type/Organization",
            "public record Organization2 { }",
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        // Act
        var folder1 = _cacheService.GetReleaseFolderPath(release1);
        var folder2 = _cacheService.GetReleaseFolderPath(release2);

        // Assert
        folder1.Should().NotBe(folder2, "Different code should produce different release folders");
    }

    [Fact]
    public void IsReleaseValid_ReturnsFalse_WhenFolderDoesNotExist()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/NonExistent",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        var folderPath = _cacheService.GetReleaseFolderPath(release);

        // Act
        var isValid = _cacheService.IsReleaseValid(folderPath);

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void IsReleaseValid_ReturnsFalse_WhenFolderIsEmpty()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Empty",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        var folderPath = _cacheService.GetReleaseFolderPath(release);
        Directory.CreateDirectory(folderPath);

        // Act
        var isValid = _cacheService.IsReleaseValid(folderPath);

        // Assert
        isValid.Should().BeFalse("Empty folder should not be valid");
    }

    [Fact]
    public void IsReleaseValid_ReturnsTrue_WhenDllExists()
    {
        // Arrange
        var release = NodeTypeRelease.Create(
            "Type/Valid",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "1.0.0");

        var folderPath = _cacheService.GetReleaseFolderPath(release);
        Directory.CreateDirectory(folderPath);

        // Create a dummy DLL
        var dllPath = Path.Combine(folderPath, $"{release.GetSanitizedPath()}.dll");
        File.WriteAllText(dllPath, "dummy dll content");

        // Act
        var isValid = _cacheService.IsReleaseValid(folderPath);

        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public void GetLockDirectory_ReturnsSubdirectory()
    {
        // Act
        var lockDir = _cacheService.GetLockDirectory();

        // Assert
        lockDir.Should().StartWith(_testCacheDir);
        lockDir.Should().EndWith(".locks");
    }
}
