using MeshWeaver.Mesh;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The ONE definition of "which modules does this tester run" — read by BOTH lanes: the gate
/// (<see cref="PluginGateRunner"/>, which activates them so package node types register) and the
/// bake (<see cref="TreeBake"/>, which compiles against them).
///
/// <para>🚨 It exists because the two lanes silently disagreed, and that disagreement reached
/// production. The gate stands up a mesh, so it composed its installed modules into the compile
/// reference set for free; the bake has no mesh and used the bare TPA baseline — and a module lives
/// under <c>modules/&lt;name&gt;/</c>, which is by construction NOT in
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. So the moment the AI engine became a module (#2276), the
/// gate went on compiling <c>Store/Installer</c>'s <c>AiSettings</c> calls while the bake could no
/// longer resolve the type at all: <b>compile-check green, publish-bake red, same content, same
/// commit</b>. Five Store NodeTypes read as content errors, no bundle was sealed for the new
/// framework identity, and every install correctly declined to self-update onto an image whose
/// content had no bake — a fleet pinned by a reference list (#2563).</para>
///
/// <para>One list, two readers: a module added for one lane cannot go missing from the other.</para>
/// </summary>
internal static class TesterModules
{
    /// <summary>
    /// Modules this IMAGE ships, laid beside the binary by the <c>MeshModuleClosure</c> lane in
    /// <c>MeshWeaver.PluginTester.csproj</c>.
    ///
    /// <para>🚨 The <c>.dll</c> suffix is the convention every host uses AND load-bearing:
    /// <see cref="MeshBuilder.ResolveModulePath(string)"/> derives the folder with
    /// <c>GetFileNameWithoutExtension</c>, so a bare <c>"MeshWeaver.AI"</c> would probe
    /// <c>modules/MeshWeaver/</c> and read as absent.</para>
    /// </summary>
    public static readonly string[] ImageShipped = ["MeshWeaver.AI.dll"];

    /// <summary>
    /// Every module entry this run activates: the image-shipped set, then the <c>--module</c>
    /// externals in the order given. Entries are names (resolved beside the binary) or absolute
    /// paths (used exactly as given, so a mounted module can never be silently substituted by an
    /// image copy — <see cref="MeshBuilder.ResolveModulePath(string)"/> passes rooted paths through).
    /// </summary>
    public static IReadOnlyList<string> Entries(IReadOnlyList<string>? external) =>
        external is null || external.Count == 0
            ? ImageShipped
            : [.. ImageShipped, .. external];

    /// <summary>
    /// <see cref="Entries"/> resolved to the assembly files on disk, for the lane that needs paths
    /// rather than configuration keys (the bake). Resolution is
    /// <see cref="MeshBuilder.ResolveModulePath(string)"/> — the same probe the mesh uses, so both
    /// lanes bind the same bytes.
    /// </summary>
    public static IReadOnlyList<string> ResolvedPaths(IReadOnlyList<string>? external) =>
        [.. Entries(external).Select(MeshBuilder.ResolveModulePath)];
}
