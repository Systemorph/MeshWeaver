using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard: no source file may hand-write a <c>/_settings…</c> URL. The global settings
/// page is registered at <c>_Setting</c> (singular, capital S —
/// <c>GlobalSettingsNodeType.SettingsPath</c>), so every <c>/_settings/…</c> literal is a link to a
/// path that does not exist and answers <i>"does not match any registered address pattern"</i>.
///
/// <para>This is not style policing — it is the exact defect of #1817, and the reason it needs a
/// guard rather than a one-time fix is that it PROPAGATED: the About page and What's New were
/// unreachable from the profile menu for months, #1791 then copied the literal into the build chip,
/// and the profile menu's bare Settings fallback carried a fourth copy nobody had noticed (the issue
/// reports three sites; there were four). A fifth is one keystroke away, and nothing else fails when
/// it happens: the page renders "Page not found" and no test, build or log names the cause.</para>
///
/// <para>The needle is deliberately CASE-SENSITIVE and requires the leading slash, which makes it
/// exact rather than approximate:</para>
/// <list type="bullet">
///   <item><c>"_settings"</c> WITHOUT a slash is a legitimate reserved satellite/schema segment in
///     the cross-schema Postgres/Snowflake routing (<c>PostgreSqlCrossSchemaQueryProvider</c>,
///     <c>SearchableSchemasUpdater</c>) — an unrelated meaning, and untouched by this guard.</item>
///   <item><c>/_Settings</c> (capital S, plural) is the per-user notification-settings satellite
///     (<c>NotificationSettingsPaths.SettingsSegment</c>) — a real path, and untouched too.</item>
/// </list>
///
/// <para>The fix for a failure here is never to suppress it, and never to re-spell the literal
/// correctly by hand: build the link from <c>GlobalSettingsNodeType.SettingsHref</c> /
/// <c>GlobalSettingsNodeType.TabHref(tabId)</c>, which derive it from the registered path.</para>
/// </summary>
public class GlobalSettingsRouteLiteralGuard
{
    /// <summary>Repo-root directories that hold hand-authored source and mesh node content.</summary>
    private static readonly string[] ScannedRoots =
        ["src", "test", "samples", "content", "memex", "clients"];

    /// <summary>Extensions a human authors a navigation target in. An allow-list, as in the NUL guard.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".cshtml", ".ts", ".tsx", ".js", ".jsx", ".html", ".md", ".json"
    };

    private static readonly string[] ExcludedSegments =
        ["bin", "obj", "node_modules", "TestResults", ".git", ".vs", "dist"];

    /// <summary>
    /// Assembled from parts so this file can name the offending shape without tripping its own scan
    /// (the same trick <c>NoLiteralNulInSourceGuard</c> uses for the NUL character).
    /// </summary>
    private static string Needle => "/_" + "settings";

    [Fact]
    public void NoSourceFileHandWritesTheUnregisteredSettingsRoute()
    {
        var root = FindRepoRoot();
        var self = typeof(GlobalSettingsRouteLiteralGuard).Name + ".cs";

        var offenders = ScannedRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => TextExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsExcluded(root, f))
            .Where(f => !string.Equals(Path.GetFileName(f), self, StringComparison.Ordinal))
            .SelectMany(f => Hits(root, f))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"A '{Needle}' URL is hand-written in source. The global settings node is registered at "
            + "'_Setting' (GlobalSettingsNodeType.SettingsPath), so this link 404s with \"does not "
            + "match any registered address pattern\" — that is #1817, which reached four call "
            + "sites because each one copied the literal. Build the link from "
            + "GlobalSettingsNodeType.SettingsHref / .TabHref(tabId) instead. Offending lines:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>Every <c>root</c>-relative <c>file:line</c> in <paramref name="path"/> holding the needle.</summary>
    private static IEnumerable<string> Hits(string root, string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break; // a file being written by a concurrent build is not evidence of anything
        }

        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(Needle, StringComparison.Ordinal))
                yield return $"  {Path.GetRelativePath(root, path)}:{i + 1}: {lines[i].Trim()}";
    }

    private static bool IsExcluded(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
