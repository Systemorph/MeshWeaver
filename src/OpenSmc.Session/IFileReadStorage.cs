namespace OpenSmc.Session;

public interface IFileReadStorage
{
    Task<Stream> ReadAsync(string filePath, CancellationToken cancellation = default);
}

public interface IFileReadStorageProvider
{
    IFileReadStorage FileStorage { get; }
}