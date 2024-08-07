using MeshWeaver.Activities;
using MeshWeaver.DataSetReader;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;

namespace MeshWeaver.Import;

/// <summary>
/// This is a request entity triggering import when executing in a data hub
/// using the Import Plugin. See also AddImport method.
/// </summary>
/// <param name="Source">Content of the source to be imported, e.g. a string (shipping the entire content) or a file name (together with StreamType = File)</param>
public record ImportRequest(Source Source) : IRequest<ImportResponse>
{
    public ImportRequest(string Contennt)
        : this(new StringStream(Contennt)) { }

    public string MimeType { get; init; } =
        MimeTypes.MapFileExtension(
            Source is StreamSource stream ? Path.GetExtension(stream.Name) : ""
        );

    public string Format { get; init; } = ImportFormat.Default;
    public object TargetDataSource { get; init; }
    public bool Snapshot { get; init; }
    public DataSetReaderOptions DataSetReaderOptions { get; init; } = new();

    internal Type EntityType { get; init; }

    public ImportRequest WithEntityType(Type entityType) => this with { EntityType = entityType };

    public bool SaveLog { get; init; }
}

public record ImportResponse(long Version, ActivityLog Log);

public abstract record Source { }

public record StringStream(string Content) : Source;

public record StreamSource(string Name, Stream Stream) : Source;

//public record FileStream(string FileName) : Source;
