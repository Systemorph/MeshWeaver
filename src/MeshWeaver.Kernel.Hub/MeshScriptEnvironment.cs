using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
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
    /// The curated assembly anchors plus the DI-contributed assemblies for one script session:
    /// every explicit <see cref="KernelScriptAssembly"/> registration AND every boot-installed
    /// module (<see cref="InstalledModuleAssembly"/>, #1653). Modules join the cell surface
    /// per-session by construction (issue #1649 part 1): a module published into
    /// <c>modules/&lt;name&gt;/</c> is a Default-ALC file-backed assembly, so the runtime bind is
    /// free — only its metadata reference has to be guaranteed here, immune to the process-wide
    /// snapshot freeze in <see cref="KernelScriptReferences"/>. De-duplication against the
    /// snapshot and the anchors happens by file path in
    /// <see cref="KernelScriptReferences.GetReferencesAsync"/>.
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
        foreach (var module in serviceProvider.GetServices<InstalledModuleAssembly>())
            assemblies.Add(module.Assembly);
        return assemblies;
    }

    /// <summary>
    /// The full, process-shared metadata reference set for a script session — the shared
    /// snapshot of every referenceable loaded assembly plus the session anchors. Safe to
    /// hand to a Roslyn workspace: every instance is the one shared materialization.
    ///
    /// <para>🚨 <paramref name="ct"/> governs only how long the CALLER waits for the shared,
    /// process-wide snapshot — never used to abort the snapshot build itself, which keeps
    /// running for the next caller regardless of this one's cancellation. See
    /// <see cref="KernelScriptReferences.GetReferencesAsync"/>.</para>
    /// </summary>
    public static Task<ImmutableArray<MetadataReference>> ReferencesAsync(
        IServiceProvider serviceProvider, CancellationToken ct)
        => KernelScriptReferences.GetReferencesAsync(SessionAssemblies(serviceProvider), ct);

    /// <summary>
    /// Whether an assembly is TEST SCAFFOLDING and must therefore be kept out of the script
    /// reference set — identified by the naming convention this repo uses: the shared fixture, the
    /// per-host test bases, and the test assemblies themselves. None exists in a deployed portal.
    ///
    /// <para>🚨 This is what keeps script compilation in a test IDENTICAL to production. The
    /// reference set is built from the assemblies loaded in the process, and a test process always
    /// has <c>MeshWeaver.Fixture</c> loaded — whose test-only <c>IMeshService.QueryAsync</c> lives
    /// in the very namespace scripts import. Without this filter, tests compile scripts against APIs
    /// production does not have: the export templates bound that method, passed eleven tests that
    /// really executed them, and threw CS1061 on the first export a user attempted.</para>
    /// </summary>
    /// <param name="assemblyName">The simple assembly name (no extension).</param>
    /// <returns>True when the assembly is test-only.</returns>
    public static bool IsTestScaffolding(string assemblyName)
        => assemblyName.Equals("MeshWeaver.Fixture", StringComparison.Ordinal)
           || assemblyName.EndsWith(".TestBase", StringComparison.Ordinal)
           || assemblyName.EndsWith(".Test", StringComparison.Ordinal)
           || assemblyName.EndsWith(".Tests", StringComparison.Ordinal);
}
