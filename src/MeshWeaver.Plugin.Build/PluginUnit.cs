using System.Collections.Immutable;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// One compilation unit of a plugin — the build-time mirror of what the portal compiles at
/// runtime for a single NodeType.
///
/// <para>🚨 The unit is a <c>Source/</c> directory, NOT a plugin. A plugin has many: UWDeepfield
/// has eleven (its root plus one per NodeType). They are compiled SEPARATELY at runtime, which is
/// why two of them may legitimately declare the same type name — <c>TaskAssignmentService</c>
/// exists in both <c>UwPortfolio/Source</c> and <c>UWDeepfieldHome/Source</c>. Merging a plugin's
/// units into one assembly produces ~200 bogus CS0111s and is the first thing anyone gets wrong.</para>
/// </summary>
/// <param name="NodePath">Mesh path of the NodeType that owns this unit (e.g. <c>UWDeepfield/UwPortfolio</c>).</param>
/// <param name="SourceDirectory">Absolute path of the unit's own <c>Source/</c> directory.</param>
/// <param name="Closure">Every directory whose <c>*.cs</c> feeds this compilation — own Source first,
/// then each resolved <c>shared=</c> include, in declaration order.</param>
/// <param name="DeclaredSources">The raw <c>sources</c> queries off the node, kept for diagnostics:
/// when a closure resolves short, the unresolved query is what you need to see.</param>
public sealed record PluginUnit(
    string NodePath,
    string SourceDirectory,
    ImmutableArray<string> Closure,
    ImmutableArray<string> DeclaredSources)
{
    /// <summary>
    /// The assembly / package-subdirectory name for this unit — the node path with separators
    /// flattened, matching the runtime's own <c>SanitizeNodeName</c> shape so a prebuilt artifact
    /// lands where the assembly store already looks for it.
    /// </summary>
    public string UnitName => NodePath.Replace('/', '_');
}
