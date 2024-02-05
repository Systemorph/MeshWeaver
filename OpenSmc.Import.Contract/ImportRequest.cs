using OpenSmc.Import.Contract.Options;
using OpenSmc.Messaging;

namespace OpenSmc.Import.Contract;

public record ImportRequest : IRequest<ImportResult>
{
    public ImportOptions Options;
}

public record ImportResult
{
}