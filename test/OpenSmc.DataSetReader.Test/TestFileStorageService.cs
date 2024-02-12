using OpenSmc.FileStorage;

namespace OpenSmc.DataSetReader.Test;

public interface ITestFileStorageService : IFileReadStorage, IFileWriteStorage
{
    public Task<Stream> GetStreamFromFilePath(string filePath);
}

public class TestFileStorageService : ITestFileStorageService
{
    public async Task<Stream> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var resource = new FileStream(filePath, FileMode.Open);

        if (Directory.Exists(filePath))
            throw new InvalidOperationException($"No such file: {filePath}");
        if (resource == null)
            throw new ArgumentException($"File {filePath} was no found");
        return await Task.FromResult((Stream)resource);
    }

    public async Task<Uri> WriteAsync(Stream stream, string filePath, CancellationToken cancellation = default)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellation);

        var directoryName = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(directoryName) && !string.IsNullOrWhiteSpace(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }

        await File.WriteAllBytesAsync(filePath, ms.ToArray(), cancellation);
        return null;
    }

    public async Task<Stream> GetStreamFromFilePath(string filePath) => await ReadAsync(filePath, CancellationToken.None);
}