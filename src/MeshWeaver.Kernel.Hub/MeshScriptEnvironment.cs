using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// The ONE description of the C# environment kernel scripts compile in — the curated
/// assembly anchors, the default imports, the globals type, and the shared metadata
/// resolver. <see cref="KernelExecutor"/> builds its <c>ScriptOptions</c> from this, and
/// the language service builds its script-flavored completion workspace from the SAME
/// values — so what the editor suggests is exactly what the kernel can run. Two copies
/// of this list drift; this type is why there is only one.
/// </summary>
public static class MeshScriptEnvironment
{
    /// <summary>
    /// The namespaces every kernel script sees without a <c>using</c> — applied via
    /// <c>ScriptOptions.WithImports</c> at run time and as the compilation's usings in the
    /// language-service script workspace.
    /// </summary>
    public static readonly ImmutableArray<string> Imports =
    [
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "System.ComponentModel",
        "System.ComponentModel.DataAnnotations",
        "System.Reactive.Linq",
        "System.Text.Json",
        "Microsoft.Extensions.Logging",
        "MeshWeaver.Application.Styles",
        "MeshWeaver.Layout",
        // LayoutAreaHost + RenderingContext live here — used by the WithView((host, ctx) => …)
        // lambda in nearly every `--render` layout cell. Companion to MeshWeaver.Layout above,
        // so authors don't have to add the extra using for the standard layout-area pattern.
        "MeshWeaver.Layout.Composition",
        "MeshWeaver.Layout.DataGrid",
        "MeshWeaver.Messaging",
        "MeshWeaver.Mesh",
    ];

    /// <summary>The globals type whose properties scripts address as bare identifiers
    /// (<c>Mesh</c>, <c>Log</c>, <c>Ct</c>, <c>Inputs</c>).</summary>
    public static Type GlobalsType => typeof(MeshScriptGlobals);

    /// <summary>
    /// The shared, process-memoized metadata resolver — REQUIRED wherever script code is
    /// compiled (run time or language service): the default resolver re-materializes the
    /// globals type's whole transitive closure of native metadata per compilation (the
    /// ~200 MiB-per-session leak documented on <see cref="KernelScriptReferences"/>).
    /// </summary>
    public static MetadataReferenceResolver MetadataResolver => SharedScriptMetadataResolver.Instance;

    /// <summary>
    /// The curated assembly anchors plus the DI-contributed module assemblies
    /// (<see cref="KernelScriptAssembly"/>) for one script session.
    /// </summary>
    public static IReadOnlyCollection<Assembly> SessionAssemblies(IServiceProvider serviceProvider)
    {
        var assemblies = new HashSet<Assembly>
        {
            typeof(IMessageHub).Assembly,
            typeof(Address).Assembly,
            typeof(UiControl).Assembly,
            typeof(DataExtensions).Assembly,
            typeof(EntityStore).Assembly,
            typeof(System.ComponentModel.DescriptionAttribute).Assembly,
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly,
            typeof(System.Reactive.Linq.Observable).Assembly,
            typeof(FluentIcons).Assembly,
            typeof(ILogger).Assembly,
            typeof(LoggerExtensions).Assembly,
            typeof(MeshScriptGlobals).Assembly,
        };
        foreach (var contrib in serviceProvider.GetServices<KernelScriptAssembly>())
            assemblies.Add(contrib.Assembly);
        return assemblies;
    }

    /// <summary>
    /// The full, process-shared metadata reference set for a script session — the shared
    /// snapshot of every referenceable loaded assembly plus the session anchors. Safe to
    /// hand to a Roslyn workspace: every instance is the one shared materialization.
    /// </summary>
    public static ImmutableArray<MetadataReference> References(IServiceProvider serviceProvider)
        => KernelScriptReferences.GetReferences(SessionAssemblies(serviceProvider));
}
