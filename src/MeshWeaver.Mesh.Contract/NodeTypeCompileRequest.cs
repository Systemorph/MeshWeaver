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

/// <summary>
/// Result of a <see cref="CreateReleaseRequest"/>. <see cref="Success"/> reports
/// whether the request was accepted (compile dispatched or already up-to-date);
/// <see cref="AlreadyUpToDate"/> short-circuits when the existing release already
/// matches the live source set.
/// </summary>
/// <param name="Success">True when the trigger was accepted (a compile started
/// or the existing release was reused). False if the hub rejected the request.</param>
/// <param name="AlreadyUpToDate">True when the existing release already matches
/// the live sources and no new compile was dispatched.</param>
/// <param name="Error">Failure reason when <paramref name="Success"/> is false.</param>
public record CreateReleaseResponse(bool Success, bool AlreadyUpToDate = false, string? Error = null);

/// <summary>
/// Triggers a script run for every test <c>Code</c> node under the NodeType's
/// <c>Test/</c> folder. Returns the list of activity paths created so the
/// caller can subscribe to each for live progress.
/// </summary>
public record RunTestsRequest : IRequest<RunTestsResponse>;

/// <summary>
/// Result of a <see cref="RunTestsRequest"/>. Carries the activity paths created
/// for each dispatched test so the caller can subscribe per-activity for live
/// progress / final status.
/// </summary>
/// <param name="ActivityPaths">Path of each <c>ActivityLog</c> MeshNode created
/// for the dispatched tests; empty when no tests were found or when the trigger
/// could not run.</param>
/// <param name="Error">Failure reason when no activities were dispatched.</param>
public record RunTestsResponse(IReadOnlyList<string> ActivityPaths, string? Error = null);

/// <summary>
/// Posted to a compile-activity hub to run the Roslyn compile for the NodeType
/// at <see cref="ParentNodeTypePath"/>. The activity hub is the **execution
/// sandbox**: it owns the long-running Roslyn invocation while the per-NodeType
/// hub and the mesh hub stay responsive. The handler reads the parent's
/// NodeTypeDefinition, runs <c>compilationService.CompileAndGetConfigurations</c>,
/// updates the activity's <c>ActivityLog</c> with progress, and finally writes
/// the terminal compile state back to the parent's MeshNode.
///
/// <para>Activity Control Plane doctrine — see
/// <c>Doc/Architecture/ActivityControlPlane.md</c>: "every long-running
/// operation runs on an Activity hub."</para>
/// </summary>
/// <param name="ParentNodeTypePath">Path of the parent NodeType MeshNode whose
/// compile state this activity will update on completion.</param>
public record RunCompileRequest(string ParentNodeTypePath) : IRequest<RunCompileResponse>;

/// <summary>
/// Result of <see cref="RunCompileRequest"/>. Reports whether the compile
/// was dispatched (the activity is now running); subscribers observe the
/// activity's <c>ActivityLog</c> for the final outcome.
/// </summary>
public record RunCompileResponse(bool Dispatched, string? Error = null);
