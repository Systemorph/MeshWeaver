namespace MeshWeaver.Data.Completion;

/// <summary>
/// Workspace reference for streaming autocomplete results from a remote hub.
/// Subscribers see incremental snapshots arrive as each <see cref="IAutocompleteProvider"/>
/// finishes producing items — fast local providers emit early, remote ones merge in
/// later, the snapshot keeps refining until everything completes.
///
/// <para>
/// Usage:
/// <code>
/// var stream = workspace.GetRemoteStream&lt;AutocompleteResponse, AutocompleteReference&gt;(
///     agentAddress, new AutocompleteReference("@/myFile", currentNamespace));
/// stream.Where(c =&gt; c.Value != null).Select(c =&gt; c.Value!).Subscribe(snapshot =&gt; ...);
/// </code>
/// </para>
///
/// <para>
/// Replaces the request/response <see cref="AutocompleteRequest"/>+<see cref="AutocompleteResponse"/>
/// pair for streaming consumers. The request/response pair is still available for
/// callers that only want the final aggregated snapshot.
/// </para>
/// </summary>
/// <param name="Query">The autocomplete query (including any prefix like "@" or "/").</param>
/// <param name="ContextPath">Path of the node from which the request is being made — providers
/// use this for proximity scoring (boosting nearby items).</param>
public record AutocompleteReference(string Query, string? ContextPath = null)
    : WorkspaceReference<AutocompleteResponse>;
