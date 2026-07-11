using System.IO;

namespace MeshWeaver.AI;

/// <summary>
/// Resolves the on-disk <b>AI content section</b> — the ship-with-the-repo folder (<c>content/ai</c>)
/// that holds the built-in <c>Agent</c> and <c>Skill</c> nodes as editable <c>.md</c> files, factored
/// out of the compiled <c>MeshWeaver.AI</c> assembly so they can be edited in the mesh and synced back
/// to the repo. When no section is found the built-in providers fall back to the EMBEDDED resources,
/// so the offline default (MAUI / monolith) never loses its agents and skills.
///
/// <para>Two shapes are resolved, in order:</para>
/// <list type="number">
///   <item><b>Repo working tree</b> (<see cref="RepoSectionRoot"/>) — <c>content/ai</c> found by
///     walking up from the running assembly. This is the dev source-of-truth: reads pick up live edits
///     and the sync-back writer commits here.</item>
///   <item><b>Copy-to-output</b> (<c>AiContent</c> beside the assembly) — the deployed offline copy,
///     shipped by the csproj <c>Content</c> item. Read-only (a container has no repo to sync back to).</item>
/// </list>
/// </summary>
internal static class AiContentLocator
{
    /// <summary>The section root (<c>content/ai</c> or the copied <c>AiContent</c>), or null → embedded.</summary>
    public static string? SectionRoot() => RepoSectionRoot() ?? OutputSectionRoot();

    /// <summary>The <b>repo working tree</b> section (<c>content/ai</c>), by walking up from the running
    /// assembly's directory. Null when not running from a checkout (a deployed container). This is the
    /// path the sync-back writer edits — only there can a dev commit the change.</summary>
    public static string? RepoSectionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "content", "ai");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // The copy-to-output section shipped beside the assembly (deployed offline). Read-only.
    private static string? OutputSectionRoot()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "AiContent");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
