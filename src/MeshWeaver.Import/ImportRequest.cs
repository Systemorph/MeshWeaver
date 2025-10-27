using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.DataSetReader;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;

namespace MeshWeaver.Import;

/// <summary>
/// This is a request entity triggering import when executing in a data hub
/// using the Import Plugin. See also AddImport method.
/// </summary>
public record ImportRequest : IRequest<ImportResponse>
{
    public ImportRequest(string content)
        : this(new StringStream(content)) { }

    /// <summary>
    /// This is a request entity triggering import when executing in a data hub
    /// using the Import Plugin. See also AddImport method.
    /// </summary>
    /// <param name="Source">Content of the source to be imported, e.g. a string (shipping the entire content) or a file name (together with StreamType = File)</param>
    [JsonConstructor]
    public ImportRequest(Source Source)
    {
        this.Source = Source;
        MimeType = MimeTypes.MapFileExtension(
            Source is CollectionSource stream ? Path.GetExtension(stream.Path) : ""
        ) ?? "";
    }

    public string MimeType { get; init; }

    public string Format { get; init; } = ImportFormat.Default;
    public object? TargetDataSource { get; init; }
    public UpdateOptions UpdateOptions { get; init; } = UpdateOptions.Default;
    public DataSetReaderOptions DataSetReaderOptions { get; init; } = new();

    /// <summary>
    /// Timeout for the import operation. If not specified, no timeout is applied.
    /// This is useful for testing scenarios where imports might hang.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    internal Type EntityType { get; init; } = null!;

    public ImportRequest WithEntityType(Type entityType) => this with { EntityType = entityType };

    public ImportRequest WithTimeout(TimeSpan timeout) => this with { Timeout = timeout };

    public bool SaveLog { get; init; }

    /// <summary>Content of the source to be imported, e.g. a string (shipping the entire content) or a file name (together with StreamType = File)</summary>
    public Source Source { get; init; }

    public void Deconstruct(out Source Source)
    {
        Source = this.Source;
    }
}

public record ImportResponse(long Version, ActivityLog Log);

public abstract record Source { }

public record StringStream(string Content) : Source;

public record CollectionSource(string Collection, string Path) : Source;

//public record FileStream(string FileName) : Source;
