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
    /// <summary>
    /// Creates a request whose source is the supplied string content (wrapped in a <see cref="StringStream"/>).
    /// </summary>
    /// <param name="content">The full content to import.</param>
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

    /// <summary>The MIME type of the source, derived from the source path's extension; selects the data-set reader.</summary>
    public string MimeType { get; init; }

    /// <summary>Optional import format key; when null, the format is inferred or defaults to <see cref="ImportFormat.Default"/>.</summary>
    public string? Format { get; init; }

    /// <summary>
    /// Optional import configuration. When provided, this configuration will be used instead of the Format string.
    /// </summary>
    public ImportConfiguration? Configuration { get; init; }

    /// <summary>Optional identifier of the data source the imported instances should be written to.</summary>
    public object? TargetDataSource { get; init; }
    /// <summary>Options controlling how imported instances are merged into the workspace.</summary>
    public UpdateOptions UpdateOptions { get; init; } = UpdateOptions.Default;
    /// <summary>Options passed to the data-set reader (e.g. delimiter, target entity type).</summary>
    public DataSetReaderOptions DataSetReaderOptions { get; init; } = new();

    /// <summary>
    /// Timeout for the import operation. If not specified, no timeout is applied.
    /// This is useful for testing scenarios where imports might hang.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    internal Type EntityType { get; init; } = null!;

    /// <summary>Returns a copy of this request targeting the given entity type.</summary>
    /// <param name="entityType">The entity type to import rows as.</param>
    /// <returns>A new request with the entity type set.</returns>
    public ImportRequest WithEntityType(Type entityType) => this with { EntityType = entityType };

    /// <summary>Returns a copy of this request with the given import timeout.</summary>
    /// <param name="timeout">The maximum duration of the import.</param>
    /// <returns>A new request with the timeout set.</returns>
    public ImportRequest WithTimeout(TimeSpan timeout) => this with { Timeout = timeout };

    /// <summary>When true, the import activity log is persisted.</summary>
    public bool SaveLog { get; init; }

    /// <summary>Content of the source to be imported, e.g. a string (shipping the entire content) or a file name (together with StreamType = File)</summary>
    public Source Source { get; init; }

}

/// <summary>
/// Response to an <see cref="ImportRequest"/>, carrying the resulting workspace version and the import activity log.
/// </summary>
/// <param name="Version">The workspace version after the import completed.</param>
/// <param name="Log">The activity log capturing import progress, warnings and errors.</param>
public record ImportResponse(long Version, ActivityLog Log);

/// <summary>
/// Base type for the various ways an import's content can be supplied.
/// </summary>
public abstract record Source { }

/// <summary>
/// A source whose content is supplied directly as a string.
/// </summary>
/// <param name="Content">The full content to import.</param>
public record StringStream(string Content) : Source;

/// <summary>
/// A source referencing a file within a content collection.
/// </summary>
/// <param name="Collection">The content collection name.</param>
/// <param name="Path">The path of the file within the collection.</param>
public record CollectionSource(string Collection, string Path) : Source;

//public record FileStream(string FileName) : Source;
