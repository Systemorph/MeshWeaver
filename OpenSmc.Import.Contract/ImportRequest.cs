using OpenSmc.Import.Contract.Options;
using OpenSmc.Messaging;

namespace OpenSmc.Import.Contract;

public record ImportRequest : IRequest<ImportResult>
{
    public string FileName { get; init; }
    public string Format { get; init; }
    public ImportOptions Options;
}

public record ImportResult
{
}