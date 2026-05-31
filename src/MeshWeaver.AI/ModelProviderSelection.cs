using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// A user's choice of which <c>ModelProvider</c> subtrees feed their chat model
/// picker + credential resolution. Persisted as the content of a single node
/// per user at <c>{userId}/_Provider/_Selection</c>
/// (see <see cref="ModelProviderNodeType.SelectionNodeId"/>). Empty/absent ⇒
/// the default set (root catalog + context + nodeType), i.e. existing behaviour.
///
/// <para>Each entry is the full path of a <c>ModelProvider</c> node the user
/// wants active (e.g. <c>acme/_Provider/Anthropic</c>,
/// <c>rbuergi/_Provider/OpenAI</c>). For a shared/org provider the user may
/// hold only <c>Read</c> on the subtree — <see cref="ChatClientCredentialResolver"/>
/// reads the (decrypted) key under a system identity, gated by that Read, so
/// the user can <i>use</i> the provider without <i>seeing</i> the raw key.</para>
/// </summary>
public record ModelProviderSelection
{
    /// <summary>Full paths of the ModelProvider nodes the user selected.</summary>
    public ImmutableArray<string> SelectedProviderPaths { get; init; } = ImmutableArray<string>.Empty;
}
