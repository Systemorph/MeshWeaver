using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

/// <summary>
/// This is a request entity triggering import when executing in a data hub
/// using the Import Plugin. See also AddImport method.
/// </summary>
/// <param name="Content">Content of the source to be imported, e.g. a string or a file name</param>
/// <param name="StreamType">Type of the source to be configured in the import plugin, e.g. a file share.</param>
public record ImportRequest(string Content) : IRequest<DataChanged>
{
    public string StreamType { get; set; } = nameof(String);
    public string MimeType { get; init; } = MimeTypes.MapFileExtension(Content);
    public string Format { get; init; } = ImportFormat.Default;
    public object TargetDataSource { get; init; }
    public bool SnapshotMode { get; init; }
    public DataSetReaderOptions DataSetReaderOptions { get; init; } = new();
}

