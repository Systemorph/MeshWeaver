using OpenSmc.Import.Options;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

public record ImportRequest : IRequest<DataChanged>
{
    public string FileName { get; init; }
    public string Format { get; init; }
    public ImportOptions Options;
}

