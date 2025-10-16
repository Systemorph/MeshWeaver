using System;
using System.IO;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class FileSystemStreamProviderTests : IDisposable
{
    private readonly string testDirectory;
    private readonly FileSystemStreamProvider provider;

    public FileSystemStreamProviderTests()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(testDirectory);
        provider = new FileSystemStreamProvider(testDirectory);
    }

    [Fact]
    public async Task GetStreamAsync_ExistingFile_ReturnsStream()
    {
        // Arrange
        var testFile = Path.Combine(testDirectory, "test.txt");
        var testContent = "Hello, World!";
        await File.WriteAllTextAsync(testFile, testContent, TestContext.Current.CancellationToken);

        // Act - Use relative path
        var stream = await provider.GetStreamAsync("test.txt", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(testContent, content);
    }

    [Fact]
    public async Task GetStreamAsync_NonExistentFile_ReturnsNull()
    {
        // Act - Use relative path
        var stream = await provider.GetStreamAsync("nonexistent.txt", TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public async Task WriteStreamAsync_CreatesFile()
    {
        // Arrange
        var testContent = "Test content";
        var contentStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(testContent));

        // Act - Use relative path
        await provider.WriteStreamAsync("output.txt", contentStream, TestContext.Current.CancellationToken);

        // Assert
        var testFile = Path.Combine(testDirectory, "output.txt");
        Assert.True(File.Exists(testFile));
        var actualContent = await File.ReadAllTextAsync(testFile, TestContext.Current.CancellationToken);
        Assert.Equal(testContent, actualContent);
    }

    [Fact]
    public async Task WriteStreamAsync_CreatesDirectory()
    {
        // Arrange
        var testContent = "Test content";
        var contentStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(testContent));

        // Act - Use relative path
        await provider.WriteStreamAsync("subdir/output.txt", contentStream, TestContext.Current.CancellationToken);

        // Assert
        var subdirectory = Path.Combine(testDirectory, "subdir");
        var testFile = Path.Combine(subdirectory, "output.txt");
        Assert.True(Directory.Exists(subdirectory));
        Assert.True(File.Exists(testFile));
    }

    [Fact]
    public void ProviderType_ReturnsFileSystem()
    {
        // Assert
        Assert.Equal("FileSystem", provider.ProviderType);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }
}

