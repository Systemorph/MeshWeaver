namespace OpenSmc.FileStorage;

public interface IFileWriteStorage
{
    Task<Uri> WriteAsync(Stream stream, string filePath, CancellationToken cancellation = default);
}

public interface IFileWriteStorageProvider
{
    IFileWriteStorage FileStorage { get; }
}