namespace MeshWeaver.ComboVerifier;

/// <summary>
/// What happens to an automatically allocated work root once a verification has run.
///
/// <para>Two directories belong to one run: the materialised combo (the work root) and its composed
/// module set beside it (<c>&lt;work root&gt;.modules</c>, MeshWeaver#4413). They are one unit: a
/// Green deletes BOTH — deleting only the first left a whole module set (DLLs, closure files,
/// native payloads) behind on every successful run — and anything else keeps BOTH, because the
/// tree and the modules it ran against are exactly what an operator inspects next.</para>
/// </summary>
public static class ComboWorkRoots
{
    /// <summary>Deletes or keeps the run's directories by its exit code (0 = Green).</summary>
    /// <param name="exitCode">The verifier's exit code.</param>
    /// <param name="workRoot">The materialised combo.</param>
    /// <param name="modulesRoot">The composed module set, or null when the combo declared none.</param>
    /// <param name="output">Where a kept directory is named.</param>
    public static void Settle(int exitCode, string workRoot, string? modulesRoot, TextWriter output)
    {
        foreach (var directory in new[] { workRoot, modulesRoot }.OfType<string>())
        {
            if (exitCode == 0)
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            else
                output.WriteLine($"kept for inspection: '{Path.GetFullPath(directory)}'");
        }
    }
}
