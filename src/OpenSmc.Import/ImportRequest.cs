using OpenSmc.Data;
using OpenSmc.DataSetReader;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

/// <summary>
/// This is a request entity triggering import when executing in a data hub
/// using the Import Plugin. See also AddImport method.
/// </summary>
/// <param name="Content">Content of the source to be imported, e.g. a string (shipping the entire content) or a file name (together with StreamType = File)</param>
/// <param name="StreamType">Type of the source to be configured in the import plugin, e.g. a file share.</param>
public record ImportRequest(string Content, string StreamType = nameof(String)) : IRequest<DataChangedEvent>
{
    public string MimeType { get; init; } = MimeTypes.MapFileExtension(StreamType != nameof(String) ? Content : string.Empty);
    public string Format { get; init; } = ImportFormat.Default;
    public object TargetDataSource { get; init; }
    public bool SnapshotMode { get; init; }
    public DataSetReaderOptions DataSetReaderOptions { get; init; } = new();
}
