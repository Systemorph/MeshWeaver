using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data
{
    /// <summary>
    /// Gets the list of domain types available in the data context.
    /// </summary>
    public record GetDomainTypesRequest : IRequest<DomainTypesResponse>;
}
/// <summary>
/// Returns the list of domain types with their descriptions.
/// </summary>
/// <param name="Types">List of type descriptions available in the domain</param>
public record DomainTypesResponse(IEnumerable<TypeDescription> Types);

