#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Separates what a <b>catalog source</b> contributed from what the <b>platform</b> ships anyway.
///
/// <para><see cref="BuiltInLanguageModelProvider.GetStaticNodes"/> emits both: the provider/model
/// nodes derived from each configured <c>{Section}:Models</c>, AND a fixed set that exists
/// regardless of configuration — the <b>Auto</b> router (its pseudo-provider and its single model)
/// and the <b>ModelTier</b> registry. Tests that ask "what does THIS config section emit" want only
/// the former; counting Auto there would make each of them silently assert the platform's shipping
/// decisions as well, and break every time those change.</para>
///
/// <para>The platform-owned entries have their own coverage in
/// <c>BuiltInLanguageModelProviderTest</c>, which deliberately selects them.</para>
/// </summary>
internal static class CatalogSourceNodes
{
    /// <summary>
    /// <see cref="BuiltInLanguageModelProvider.GetStaticNodes"/> minus the platform-owned entries
    /// (the Auto router and the tier registry).
    /// </summary>
    /// <param name="provider">The provider under test.</param>
    /// <returns>Only the nodes a configured catalog source produced.</returns>
    public static IReadOnlyList<MeshNode> SourceNodes(this BuiltInLanguageModelProvider provider) =>
        provider.GetStaticNodes().Where(IsFromACatalogSource).ToList();

    /// <summary>
    /// False for the tier registry, for the router model, and for the router's pseudo-provider —
    /// the three shapes the platform emits with no configuration behind them.
    /// </summary>
    /// <param name="node">A node from the static-node enumeration.</param>
    /// <returns>True when a configured catalog source produced it.</returns>
    private static bool IsFromACatalogSource(MeshNode node) =>
        !string.Equals(node.NodeType, ModelTierNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase)
        && (node.Content as ModelDefinition)?.IsRouter != true
        && !(string.Equals(node.NodeType, ModelProviderNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase)
             && string.Equals(node.Id, LanguageModelNodeType.RouterProviderName, System.StringComparison.OrdinalIgnoreCase));
}
