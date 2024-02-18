using Microsoft.Extensions.Options;

namespace OpenSmc.Session;

public class SessionFileStorage(IOptions<SessionOptions> sessionOptions) : ISessionFileStorage
{
    private readonly SessionOptions sessionOptions = sessionOptions.Value;

    public static string BasePath = Path.Join(Path.GetTempPath(), "session");

    public static string GetSessionFilePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath, BasePath);

        if (Path.GetRelativePath(BasePath, fullPath).StartsWith(".."))
            throw new ArgumentException("Invalid path");
                    
        return fullPath;
    }

    public Task<Stream> ReadAsync(string filePath, CancellationToken cancellation = default)
    {
        var fullPath = GetSessionFilePath(filePath);
        try
        {
            return Task.FromResult<Stream>(File.OpenRead(fullPath));
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"File {filePath} was not found");
        }
    }

    public async Task<Uri> WriteAsync(Stream stream, string filePath, CancellationToken cancellation = default)
    {
        var fullPath = GetSessionFilePath(filePath);
        if (!Directory.Exists(BasePath))
            Directory.CreateDirectory(BasePath);

        await using var fileStream = File.OpenWrite(fullPath);
        await stream.CopyToAsync(fileStream, cancellation);

        return new Uri($"/api/session/{sessionOptions.SessionId}/file/download?path={filePath}", UriKind.Relative);
    }
}