#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Which plugin repos a local install serves must not depend on an environment variable a
/// developer has to remember.</b>
///
/// <para><c>plugin_repo_paths</c> mounted exactly one directory — the sibling literally named
/// <c>MeshWeaver.Plugins</c> — and offered <c>MEMEX_PLUGIN_REPOS</c> as the only way to serve
/// anything else. A developer with the course repo checked out beside it therefore had a portal
/// that could not serve a single course, with nothing to indicate why: the Store simply listed
/// less. Setting the variable fixed it until the next <c>memex-local update</c> typed without it,
/// at which point the courses silently went away again. Reported 2026-08-31 after exactly that
/// sequence.</para>
///
/// <para><b>Discovery is by CONTENT, never by name.</b> A node repo is one that has at least one
/// <c>&lt;Folder&gt;/index.json</c> declaring a root node type — the same predicate the registry's
/// own <c>NodeRepoPackageSource</c> applies (<c>Space</c>, <c>Store/Plugin</c>,
/// <c>Store/Catalog</c>). That is what makes the platform checkout and its worktrees exclude
/// themselves: they carry no root manifest at that depth, so no name list has to enumerate them —
/// and a repo added or renamed tomorrow is picked up with no change here.</para>
/// </summary>
public class MemexLocalDiscoversNodeRepoSiblingsGuard : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "memex-local-discovery-" + Guid.NewGuid().ToString("N")[..8]);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Script() =>
        Path.Combine(RepoRoot(), "deploy", "homebrew", "bin", "memex-local");

    public MemexLocalDiscoversNodeRepoSiblingsGuard()
    {
        // The platform checkout itself: the marker resolve_repo keys on, and no root manifest.
        Directory.CreateDirectory(Path.Combine(root, "Platform"));
        File.WriteAllText(Path.Combine(root, "Platform", "MeshWeaver.slnx"), "<Solution />");

        // A worktree of it — same shape, and just as wrong to mount.
        Directory.CreateDirectory(Path.Combine(root, "Platform-wt"));
        File.WriteAllText(Path.Combine(root, "Platform-wt", "MeshWeaver.slnx"), "<Solution />");

        NodeRepo("Acme.Courses", "Course", "Store/Plugin");
        NodeRepo("Acme.Views", "Pack", "Space");

        // A plain directory — nothing to serve.
        Directory.CreateDirectory(Path.Combine(root, "Notes"));

        // A manifest at the WRONG DEPTH. The registry reads <Folder>/index.json and nothing deeper,
        // so a repo whose only manifest is nested is not a node repo.
        Directory.CreateDirectory(Path.Combine(root, "Deep.Repo", "Sub", "Nested"));
        File.WriteAllText(Path.Combine(root, "Deep.Repo", "Sub", "Nested", "index.json"),
            """{ "id": "Nested", "nodeType": "Store/Plugin" }""");

        // Right depth, wrong node type — a content node, not a package root.
        NodeRepo("Not.A.Repo", "Thing", "Markdown");
    }

    private void NodeRepo(string repo, string package, string nodeType)
    {
        Directory.CreateDirectory(Path.Combine(root, repo, package));
        File.WriteAllText(Path.Combine(root, repo, package, "index.json"),
            $$"""{ "id": "{{package}}", "nodeType": "{{nodeType}}" }""");
    }

    /// <summary>
    /// Runs the shipped script's own function, sourced, rather than asserting on its text — the
    /// question is what it RESOLVES, and only running it answers that.
    /// </summary>
    private IReadOnlyList<string> Discovered(string? overrideVar = null)
    {
        var info = new ProcessStartInfo("/bin/bash",
            ["-c", $". '{Script()}'; plugin_repo_paths"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["MEMEX_LOCAL_LIB"] = "1";
        info.Environment["MEMEX_REPO"] = Path.Combine(root, "Platform");
        if (overrideVar is not null) info.Environment["MEMEX_PLUGIN_REPOS"] = overrideVar;

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "memex-local did not finish resolving plugin repos");
        Assert.True(process.ExitCode == 0,
            $"sourcing memex-local failed (exit {process.ExitCode}). stderr:\n{stderr}");

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Path.GetFileName(line.Trim()))
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void EveryNodeRepoBesideTheCheckout_IsServed_WithNoEnvironmentVariable()
    {
        Assert.Equal(["Acme.Courses", "Acme.Views"], Discovered());
    }

    /// <summary>
    /// 🚨 The platform checkout is not a node repo and mounting it would hand the portal the whole
    /// source tree. It excludes itself on content — no root manifest — so this holds for its
    /// worktrees too, which look identical and are just as wrong.
    /// </summary>
    [Fact]
    public void ThePlatformCheckoutAndItsWorktrees_AreNeverServed()
    {
        var discovered = Discovered();

        Assert.DoesNotContain("Platform", discovered);
        Assert.DoesNotContain("Platform-wt", discovered);
    }

    /// <summary>
    /// Discovery is the default, not a takeover: an explicit list still decides, so a developer who
    /// wants fewer repos than they have checked out keeps saying so.
    /// </summary>
    [Fact]
    public void AnExplicitList_StillWins()
    {
        Assert.Equal(["Acme.Views"], Discovered(Path.Combine(root, "Acme.Views")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
