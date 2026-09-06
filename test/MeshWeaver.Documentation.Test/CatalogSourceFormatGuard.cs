using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Every reader of a declared source resolves its format through ONE rule.</b>
///
/// <para>A <c>PluginCatalog</c> node is read by two different pieces of code — the browse view
/// (<c>CatalogLayoutAreas</c>) and <c>PluginUpdateWatcher</c> — and before #3384 they disagreed
/// about the same record. The view called <c>FromRepo</c> without a format, taking the parameter's
/// <c>false</c> default and reading the repository as a <c>package.json</c> manifest repo; the
/// watcher passed <c>nodeRepo: true</c> unconditionally. A catalog over a local node-repo checkout
/// therefore rendered <i>"No installable packages found."</i> while its own watcher listed those
/// same packages without complaint.</para>
///
/// <para><b>Why a guard and not just the fix.</b> The defect is not that one call site was wrong —
/// it is that the format was decided independently at each call site, so the two could drift apart
/// again the moment a third reader appears. The invariant that makes that impossible is mechanical:
/// <c>PackageSources.FromRepo</c>'s <c>nodeRepo</c> argument always comes from
/// <see cref="PackageSources.IsNodeRepoFormat"/>, never from a literal. That is what this pins.</para>
///
/// <para>🚨 The <c>bool</c> parameter defaults to <c>false</c>, which is the WRONG default for a
/// declared source — the shipped format is <c>node-repo</c>. So "forgot to pass it" and "asked for
/// package.json" are the same text at a call site, and only this guard tells them apart.</para>
/// </summary>
public class CatalogSourceFormatGuard
{
    private const string Subject = "src/MeshWeaver.PluginCatalog";

    // Every call site found by the scan below. A floor, because a matcher that stops matching
    // reports a clean tree — the failure mode this whole file exists to prevent.
    private const int KnownCallSites = 3;

    [Theory]
    // Nothing declared: the shipped format wins. This is the case #3384 got wrong.
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("node-repo", true)]
    [InlineData("NODE-REPO", true)]
    // The manifest repo opts IN, explicitly, and case does not matter.
    [InlineData("package-json", false)]
    [InlineData("Package-Json", false)]
    // An unrecognised value is not silently package.json — it falls back to the shipped format.
    [InlineData("something-else", true)]
    public void TheFormatRule_DefaultsToTheShippedNodeRepoFormat(string? format, bool expected) =>
        Assert.Equal(expected, PackageSources.IsNodeRepoFormat(format));

    /// <summary>
    /// A catalog node can SAY what it is. Before #3384 the record had no format field at all, so
    /// "the view picked wrong" was not even fixable at the node — the information did not exist.
    /// </summary>
    [Fact]
    public void ACatalogNode_CanDeclareItsFormat()
    {
        Assert.True(PackageSources.IsNodeRepoFormat(new PluginCatalogContent().Format));
        Assert.False(PackageSources.IsNodeRepoFormat(
            new PluginCatalogContent { Format = "package-json" }.Format));
    }

    /// <summary>
    /// No call site decides the format for itself: each passes the shared rule's answer.
    /// </summary>
    [Fact]
    public void EveryFromRepoCallSite_ResolvesTheFormatThroughTheSharedRule()
    {
        var root = SourceScan.FindRepoRoot();
        var dir = Path.Combine(root, Subject.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(dir),
            $"{Subject} is gone — this guard now checks nothing. Point it at the code's new home.");

        var offenders = new List<string>();
        var found = 0;

        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(path));
            var relative = SourceScan.Relative(root, path);
            foreach (var (args, index) in CallsTo(code, "FromRepo"))
            {
                // The declaration itself, not a call.
                if (args.Contains("this IMessageHub", StringComparison.Ordinal)
                    || args.Contains("ILogger? logger", StringComparison.Ordinal))
                    continue;

                found++;
                if (!args.Contains("IsNodeRepoFormat", StringComparison.Ordinal))
                    offenders.Add($"{relative}:{LineOf(code, index)} — FromRepo({Squash(args)})");
            }
        }

        Assert.True(offenders.Count == 0,
            "A PackageSources.FromRepo call site decides the repository's format for itself instead "
            + "of resolving it through PackageSources.IsNodeRepoFormat. The nodeRepo parameter "
            + "defaults to FALSE — the package.json format — while the shipped format is node-repo, "
            + "so omitting it (or passing a literal) makes this reader disagree with every other "
            + "reader of the same declared source. That is #3384: a catalog node rendered \"No "
            + "installable packages found.\" while its own update watcher listed those packages. "
            + "Pass PackageSources.IsNodeRepoFormat(<the declared Format>)."
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  · " + o)));

        Assert.True(found >= KnownCallSites,
            $"Expected at least {KnownCallSites} FromRepo call sites under {Subject}, saw {found}. "
            + "The matcher has gone blind — most likely the factory was renamed — so this guard is "
            + "reporting a clean tree having checked nothing. Re-point it before trusting it.");
    }

    /// <summary>
    /// The matcher sees the shape it claims to. Without this, every assertion above passes on a
    /// scan that silently matched nothing.
    /// </summary>
    [Fact]
    public void TheMatcher_SeesAHardcodedFormatAndAResolvedOne()
    {
        const string hardcoded = "var s = PackageSources.FromRepo(hub, p, sub, logger, nodeRepo: true);";
        const string resolved =
            "var s = PackageSources.FromRepo(hub, p, sub, logger, PackageSources.IsNodeRepoFormat(c.Format));";
        const string wrapped = "var s = PackageSources.FromRepo(\n    hub, p, sub, logger,\n    nodeRepo: true);";

        Assert.Single(CallsTo(hardcoded, "FromRepo"));
        Assert.DoesNotContain("IsNodeRepoFormat", CallsTo(hardcoded, "FromRepo").Single().Args);
        Assert.Contains("IsNodeRepoFormat", CallsTo(resolved, "FromRepo").Single().Args);
        // A call split across lines is the shape the real code uses — the scan must not stop at \n.
        Assert.DoesNotContain("IsNodeRepoFormat", CallsTo(wrapped, "FromRepo").Single().Args);
    }

    // Every call to `name(` with its full, paren-balanced argument list — calls span lines here, so
    // a line-wise regex would miss exactly the ones that matter.
    private static List<(string Args, int Index)> CallsTo(string code, string name)
    {
        var results = new List<(string, int)>();
        var needle = name + "(";
        for (var i = code.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = code.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            // `XFromRepo(` is a different method.
            if (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_'))
                continue;

            var open = i + needle.Length - 1;
            var depth = 0;
            for (var j = open; j < code.Length; j++)
            {
                if (code[j] == '(') depth++;
                else if (code[j] == ')' && --depth == 0)
                {
                    results.Add((code[(open + 1)..j], i));
                    break;
                }
            }
        }
        return results;
    }

    private static int LineOf(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;

    private static string Squash(string args) =>
        string.Join(' ', args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
