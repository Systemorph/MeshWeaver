using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Gets the JSON schema for a specific type.
/// </summary>
/// <param name="Type"></param>
public record GetSchemaRequest(string Type) : IRequest<SchemaResponse>;

/// <summary>
/// Returns the JSON schema for a specific type.
/// </summary>
/// <param name="Type"></param>
/// <param name="Schema"></param>
public record SchemaResponse(string Type, string Schema);

