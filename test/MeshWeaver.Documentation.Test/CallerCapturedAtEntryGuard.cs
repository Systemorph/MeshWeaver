using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The caller-facing read surfaces resolve "the caller" in ONE place, at their entry</b>
/// (MeshWeaver#6570).
///
/// <para>#6570 was a read that took the caller's identity from the ambient
/// <c>AccessContext</c> inside the continuation of an earlier response: there the identity is the
/// delivering thread's, the read gates pass a hub principal through, and an ungranted caller was
/// served nodes they hold no Read on. The sweep that followed found the same shape in the
/// autocomplete orchestrator, the layout-area render, the content listing and the Update
/// pre-flight. Every one of them is fixed the same way — capture once with <c>CaptureCaller()</c>
/// at the public entry, hand the value down explicitly.</para>
///
/// <para>This ratchet keeps it that way for the files that serve the MCP tools and
/// <c>/api/mesh/*</c>: an ambient read (<c>.Context ??</c>) may appear only inside the capture
/// helper itself, or in the synchronous prologue of the named entries below, which read it once
/// before composing anything. A new ambient read anywhere else is red, and the fix is to use the
/// captured value instead — never to add the method to the list without reading it.</para>
/// </summary>
public class CallerCapturedAtEntryGuard(ITestOutputHelper output)
{
    /// <summary>The files whose verbs serve a remote caller.</summary>
    private static readonly ImmutableArray<string> Surfaces =
    [
        "src/MeshWeaver.Mesh.Operations/MeshOperations.cs",
        "src/MeshWeaver.Mesh.Operations/MeshOperations.RunTests.cs",
        "src/MeshWeaver.Hosting/Completion/ChatCompletionOrchestrator.cs",
    ];

    /// <summary>
    /// The methods allowed to read the ambient identity: the capture helper, and entries that read
    /// it once in their synchronous prologue (an admin verb's "who is asking", <c>whoami</c>'s
    /// name/email echo) before any observable is composed.
    /// </summary>
    private static readonly ImmutableHashSet<string> Allowed =
    [
        "CaptureCaller",
        "WhoAmI",
        "ReloadModule",
        "UninstallPackage",
        "RebootInstance",
    ];

    private static readonly Regex AmbientRead = new(@"\.Context\s*\?\?", RegexOptions.Compiled);

    private static readonly Regex MethodDeclaration = new(
        @"^\s*(?:public|private|internal|protected)\b[^=;{]*?\b(?<name>[A-Z]\w*)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Every ambient-identity read in <paramref name="lines"/> with the method it sits in — comment
    /// lines excluded, since the documentation names the pattern it forbids.
    /// </summary>
    internal static IEnumerable<(int Line, string Method)> AmbientReads(IReadOnlyList<string> lines)
    {
        var method = "(file scope)";
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (MethodDeclaration.Match(line) is { Success: true } m)
                method = m.Groups["name"].Value;
            var code = line.TrimStart();
            if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal))
                continue;
            if (AmbientRead.IsMatch(line))
                yield return (i + 1, method);
        }
    }

    [Fact]
    public void CallerFacingSurfaces_ReadTheAmbientIdentityOnlyAtTheirEntry()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = new List<string>();
        var seen = 0;
        foreach (var relative in Surfaces)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue(
                $"the guard's subject '{relative}' moved — a guard whose roots did not follow it checks nothing");
            foreach (var (line, method) in AmbientReads(File.ReadAllLines(path)))
            {
                seen++;
                output.WriteLine($"{relative}:{line} in {method}");
                if (!Allowed.Contains(method))
                    offenders.Add($"{relative}:{line} in {method}");
            }
        }

        seen.Should().BeGreaterThan(0,
            "CaptureCaller itself reads the ambient identity — zero reads means the scan matched nothing");
        offenders.Should().BeEmpty(
            "a caller-facing verb must decide access for the caller captured at its entry "
            + "(CaptureCaller) and hand it down; an ambient read elsewhere can run inside a response "
            + "continuation, where the identity is the delivering hub's (MeshWeaver#6570)");
    }

    [Fact]
    public void TheDetector_FlagsAnAmbientReadInsideAContinuation()
    {
        var sample = new[]
        {
            "    public IObservable<string> Leaky(string path)",
            "    {",
            "        // a comment naming accessService?.Context ?? is not code",
            "        return Resolve(path).SelectMany(r =>",
            "        {",
            "            var caller = accessService?.Context ?? accessService?.CircuitContext;",
            "            return Read(r, caller);",
            "        });",
            "    }",
        };

        var reads = AmbientReads(sample).ToList();
        reads.Should().ContainSingle("the comment line names the pattern but is not code");
        reads[0].Should().Be((6, "Leaky"), "the read inside the continuation is the defect the guard exists for");
    }
}
