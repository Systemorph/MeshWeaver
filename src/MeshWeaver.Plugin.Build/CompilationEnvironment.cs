using System.Collections.Immutable;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// The ambient environment the portal gives an in-mesh compilation, restated as MSBuild inputs.
///
/// <para><b>Why this class exists.</b> At runtime a NodeType compiles against the portal's WHOLE
/// loaded assembly set, and its generated skeleton contributes a using preamble. Neither is
/// implicit in a <c>.csproj</c>, so a build that omits them fails on code that is perfectly
/// correct — and fails in ways that read like author error rather than harness error. The two
/// canonical mis-reads:</para>
/// <list type="bullet">
///   <item><description><c>CS0616 'MeshNode' is not an attribute class</c> — <see cref="Usings"/>
///   lacked <c>MeshWeaver.Domain</c>, so <c>[MeshNode(…)]</c> bound to the <c>MeshNode</c> RECORD in
///   <c>MeshWeaver.Mesh</c> instead of <c>MeshNodeAttribute</c>. Forty-seven units failed this way.</description></item>
///   <item><description><c>CS0246</c> on <c>IPackageSource</c> / <c>PluginCatalogOptions</c> —
///   <see cref="PackageIds"/> carried only <c>MeshWeaver.Graph</c>. In-mesh code may use ANY
///   framework type the portal has loaded; Store alone drops from 40 errors to 1 once the rest are
///   referenced.</description></item>
/// </list>
/// </summary>
public static class CompilationEnvironment
{
    /// <summary>
    /// The using preamble the runtime skeleton emits (see <c>DynamicMeshNodeAttributeGenerator</c>),
    /// plus the two namespaces empirically required by shipped plugin source that relies on them
    /// without a file-level using: <c>System.Reactive</c> (<c>Unit</c>) and
    /// <c>System.Globalization</c> (<c>CultureInfo</c>).
    ///
    /// <para>🚨 This is NOT <c>MeshScriptEnvironment.Imports</c>. That set belongs to the kernel's
    /// SCRIPT environment; it is narrower and using it here reintroduces the CS0616 above.</para>
    /// </summary>
    public static ImmutableArray<string> Usings { get; } =
    [
        "System",
        "System.Collections.Generic",
        "System.Collections.Immutable",
        "System.ComponentModel",
        "System.ComponentModel.DataAnnotations",
        "System.Globalization",
        "System.Linq",
        "System.Reactive",
        "System.Reactive.Linq",
        "System.Text.Json",
        "System.Text.Json.Serialization",
        "MeshWeaver.Application.Styles",
        "MeshWeaver.ContentCollections",
        "MeshWeaver.Data",
        // The one that makes [MeshNode(…)] an ATTRIBUTE — see the class remarks.
        "MeshWeaver.Domain",
        "MeshWeaver.Graph",
        "MeshWeaver.Graph.Configuration",
        "MeshWeaver.Layout",
        "MeshWeaver.Layout.Composition",
        "MeshWeaver.Layout.DataGrid",
        "MeshWeaver.Layout.Domain",
        "MeshWeaver.Layout.Views",
        "MeshWeaver.Mesh",
        "MeshWeaver.Mesh.Services",
        "MeshWeaver.Messaging",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Logging",
    ];

    /// <summary>
    /// The framework packages a plugin compiles against — the build-time stand-in for "every
    /// assembly the portal has loaded". Deliberately broader than any single plugin needs: an
    /// unused reference costs a restore entry, a missing one costs a false compile error that
    /// looks like broken plugin code.
    /// </summary>
    public static ImmutableArray<string> PackageIds { get; } =
    [
        "MeshWeaver.AI",
        "MeshWeaver.ContentCollections.Indexing",
        "MeshWeaver.Graph",
        "MeshWeaver.Import",
        "MeshWeaver.PluginCatalog",
    ];
}
