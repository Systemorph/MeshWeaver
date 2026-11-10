namespace MeshWeaver.ComboVerifier;

/// <summary>
/// What happens to an automatically allocated work root once a verification has run.
///
/// <para>Three directories belong to one run: the materialised combo (the work root), its composed
/// module set beside it (<c>&lt;work root&gt;.modules</c>, MeshWeaver#4413) and the run's mesh-free
/// bake (<c>&lt;work root&gt;.bake</c>, <see cref="DockerImageGate.BakeRootFor"/>). They are one unit: a
/// Green deletes all three — deleting only the first left a whole module set (DLLs, closure files,
/// native payloads) behind on every successful run — and anything else keeps all three, because the
/// tree, the modules and the bake it ran against are exactly what an operator inspects next.</para>
///
/// <para>A directory the run never created (a run that failed before its gate wrote a bake, a combo
/// that declared no module) is neither deleted nor named: "kept for inspection" pointing at a
/// directory that does not exist would send the operator to look for nothing.</para>
/// </summary>
public static class ComboWorkRoots
{
    /// <summary>Deletes or keeps the run's directories by its exit code (0 = Green).</summary>
    /// <param name="exitCode">The verifier's exit code.</param>
    /// <param name="workRoot">The materialised combo.</param>
    /// <param name="modulesRoot">The composed module set, or null when the combo declared none.</param>
    /// <param name="output">Where a kept directory is named.</param>
    /// <param name="bakeRoot">The run's mesh-free bake, or null when none was written.</param>
    public static void Settle(
        int exitCode, string workRoot, string? modulesRoot, TextWriter output, string? bakeRoot = null)
    {
        foreach (var directory in new[] { workRoot, modulesRoot, bakeRoot }.OfType<string>())
        {
            if (!Directory.Exists(directory))
                continue;
            if (exitCode == 0)
                Directory.Delete(directory, recursive: true);
            else
                output.WriteLine($"kept for inspection: '{Path.GetFullPath(directory)}'");
        }
    }
}
