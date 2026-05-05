using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Explicit compile trigger posted to a NodeType's own hub. The handler
/// compares <c>NodeTypeDefinition.CompiledSources</c> against the current
/// live source nodes; when they match and <see cref="Force"/> is false it
/// returns <see cref="CreateReleaseResponse.AlreadyUpToDate"/>. Otherwise
/// it flips <c>CompilationStatus = Pending</c> so the CompileWatcher starts Roslyn.
/// </summary>
public record CreateReleaseRequest(bool Force = false) : IRequest<CreateReleaseResponse>;

public record CreateReleaseResponse(bool Success, bool AlreadyUpToDate = false, string? Error = null);

/// <summary>
/// Triggers a script run for every test <c>Code</c> node under the NodeType's
/// <c>Test/</c> folder. Returns the list of activity paths created so the
/// caller can subscribe to each for live progress.
/// </summary>
public record RunTestsRequest : IRequest<RunTestsResponse>;

public record RunTestsResponse(IReadOnlyList<string> ActivityPaths, string? Error = null);
