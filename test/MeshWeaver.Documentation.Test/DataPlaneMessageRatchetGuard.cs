#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The data-plane MESSAGES are stream plumbing, and code outside the data layer is being moved
/// off them — this ratchet makes sure the count only ever goes DOWN.
///
/// <para><b>What is ratcheted.</b> Five message types from <c>MeshWeaver.Data.Contract</c>:
/// <c>GetDataRequest</c>, <c>GetDataResponse</c>, <c>DataChangeRequest</c>,
/// <c>PatchDataChangeRequest</c> and <c>DataChangedEvent</c>. Reading ONE node is
/// <c>workspace.GetMeshNodeStream(path)</c>; writing one is
/// <c>GetMeshNodeStream(path).Update(current =&gt; …)</c>; creating or replacing one is the
/// lifecycle verbs on <c>IMeshService</c>. A bespoke request/response over these messages is a
/// second read/write path beside the sanctioned one — with its own identity-propagation surface,
/// which is where the incident family "Portal (reads) hub posts GetDataRequest … with no
/// AccessContext" comes from.</para>
///
/// <para><b>The target</b> (Doc/Architecture/DataPlaneMessagesAreStreamPlumbing): every count in
/// <see cref="AllowFileName"/> reaches ZERO, after which the five types become <c>internal</c> to
/// the data layer. The data layer itself — <c>src/MeshWeaver.Data</c> and
/// <c>src/MeshWeaver.Data.Contract</c>, where the messages are defined, registered and served — is
/// the one place they belong, so it is not scanned.</para>
///
/// <para>🚨 In-mesh C# (NodeType sources, scripts, installed course cells) can reference these
/// types INVISIBLY to this scan and to <c>dotnet build</c>. That is why this is a ratchet towards
/// <c>[Obsolete]</c> and <c>internal</c>, never a delete — see the doc page for the sweep that has to
/// precede the flip.</para>
/// </summary>
public class DataPlaneMessageRatchetGuard(ITestOutputHelper output)
{
    /// <summary>The message types, as identifiers. Word-bounded, so <c>DataChangeRequest</c> never
    /// counts the <c>DataChangeRequest</c> inside <c>PatchDataChangeRequest</c>.</summary>
    internal static readonly ImmutableArray<string> MessageTypes =
    [
        "GetDataRequest",
        "GetDataResponse",
        "DataChangeRequest",
        "PatchDataChangeRequest",
        "DataChangedEvent",
    ];

    /// <summary>
    /// Per-type budgets: the SUM of the allow file's lines for that type may never exceed these.
    /// Per-line entries stop a new reference in a file that already carries one; these stop the
    /// inventory as a WHOLE from growing — including by adding a new file's line. Lower a budget
    /// in the same change that lowers or deletes a line. Seeded EXACT against the allow file, so
    /// there is no free slot.
    /// </summary>
    private static readonly ImmutableDictionary<string, int> TotalBudget = new Dictionary<string, int>
    {
        ["GetDataRequest"] = 18,
        ["GetDataResponse"] = 26,
        ["DataChangeRequest"] = 11,
        ["PatchDataChangeRequest"] = 0,
        ["DataChangedEvent"] = 0,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Production roots — and <c>samples/</c>, whose <c>Data/</c> trees are IN-MESH C# that
    /// compiles at runtime (a NodeType's <c>Source/*.cs</c>) and is invisible to <c>dotnet build</c>.
    /// <c>test/</c> is out of scope here: a test of the data layer legitimately speaks its messages,
    /// and the flip to <c>internal</c> gives the data layer's own test projects
    /// <c>InternalsVisibleTo</c>.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex", "tools", "samples"];

    /// <summary>The data layer — where the messages are defined, registered and served. The end
    /// state keeps them here and only here.</summary>
    private static readonly string[] HomeRoots = ["src/MeshWeaver.Data/", "src/MeshWeaver.Data.Contract/"];

    private const string AllowFileName = "DataPlaneMessageSites.allow";

    [Fact]
    public void NoNewReferenceToADataPlaneMessageOutsideTheDataLayer()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = ReadAllow(Path.Combine(root, "test", AllowFileName));
        var found = Scan(root);

        var failures = new List<string>();
        foreach (var ((type, file), count) in found.OrderBy(kv => kv.Key.Type, StringComparer.Ordinal)
                     .ThenBy(kv => kv.Key.File, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue((type, file), out var budget))
                failures.Add($"  NEW        {type}\t{file}\t{count} — read a node with "
                    + "GetMeshNodeStream(path), write it with GetMeshNodeStream(path).Update(…), create "
                    + "or replace it with IMeshService. Do NOT add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add($"  MORE       {type}\t{file}\t{count} > {budget} allowed — a reference was "
                    + "ADDED to a file that already carries one.");
        }

        foreach (var type in MessageTypes)
        {
            var total = allowed.Where(kv => kv.Key.Type == type).Sum(kv => kv.Value);
            var budget = TotalBudget[type];
            if (total > budget)
                failures.Add($"  TOTAL      {type}: {total} allowances > {budget} budgeted — the inventory "
                    + "GREW. Adding a line to " + AllowFileName + " is not a fix.");
        }

        // Stale entries are REPORTED, never failed: shrinking is the direction this guard exists to
        // encourage, and failing on it would red main whenever two PRs convert sites concurrently.
        foreach (var ((type, file), budget) in allowed.OrderBy(kv => kv.Key.Type, StringComparer.Ordinal)
                     .ThenBy(kv => kv.Key.File, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault((type, file), 0);
            if (count < budget)
                output.WriteLine($"STALE (please tidy): {type}\t{file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget[\"{type}\"] by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "A data-plane message was referenced outside the data layer. These messages are stream "
            + "PLUMBING (Doc/Architecture/DataPlaneMessagesAreStreamPlumbing): a node read is "
            + "GetMeshNodeStream(path), a node write is GetMeshNodeStream(path).Update(…), and "
            + "create/replace goes through IMeshService.\n"
            + string.Join("\n", failures)
            + "\n\nCurrent inventory, in allow-file form:\n"
            + string.Join("\n", found.OrderBy(kv => kv.Key.Type, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.File, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key.Type}\t{kv.Key.File}\t{kv.Value}")));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run: the scan must actually SEE references. The data layer
    /// is excluded from the ratchet, but it is where every type is defined — so scanning it must
    /// find each of the five, or the matcher (or the masker, or the roots) is broken and the ratchet
    /// above would pass on no evidence.
    /// </summary>
    [Fact]
    public void TheScannerSeesEveryTypeInTheDataLayer()
    {
        var root = SourceScan.FindRepoRoot();
        var counts = MessageTypes.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        foreach (var file in SourceScan.SourceFiles(root, HomeRoots.Select(h => h.TrimEnd('/'))))
            foreach (var (type, count) in CountIn(File.ReadAllText(file)))
                counts[type] += count;

        foreach (var type in MessageTypes)
            Assert.True(counts[type] > 0,
                $"The scanner found no reference to {type} in the data layer, where it is defined. "
                + "Either the type moved — update HomeRoots and this guard — or the matcher is broken "
                + "and the ratchet would pass having checked nothing.");
    }

    /// <summary>What the matcher counts, and what it must not.</summary>
    [Fact]
    public void TheMatcherCountsCodeNotProse()
    {
        Assert.Equal(1, CountIn("hub.Post(new DataChangeRequest { Updates = [x] });")["DataChangeRequest"]);
        Assert.Equal(0, CountIn("hub.Post(new PatchDataChangeRequest(r, p));")["DataChangeRequest"]);
        Assert.Equal(1, CountIn("hub.Post(new PatchDataChangeRequest(r, p));")["PatchDataChangeRequest"]);
        Assert.Equal(2, CountIn(".WithHandler<GetDataRequest>(H);\nvoid H(IMessageDelivery<GetDataRequest> d) { }")["GetDataRequest"]);
        Assert.Equal(0, CountIn("// posts a GetDataRequest to the owner")["GetDataRequest"]);
        Assert.Equal(0, CountIn("/// <see cref=\"GetDataResponse\"/>")["GetDataResponse"]);
        Assert.Equal(0, CountIn("var s = \"DataChangedEvent\";")["DataChangedEvent"]);
    }

    internal static Dictionary<string, int> CountIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        return MessageTypes.ToDictionary(
            t => t,
            t => Regex.Matches(code, $@"\b{t}\b", RegexOptions.CultureInvariant).Count,
            StringComparer.Ordinal);
    }

    private static Dictionary<(string Type, string File), int> Scan(string root)
    {
        var result = new Dictionary<(string Type, string File), int>();
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            var relative = SourceScan.Relative(root, file);
            if (HomeRoots.Any(h => relative.StartsWith(h, StringComparison.Ordinal)))
                continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; } // a file a concurrent build is writing is not evidence

            if (!MessageTypes.Any(t => text.Contains(t, StringComparison.Ordinal)))
                continue;

            foreach (var (type, count) in CountIn(text))
                if (count > 0)
                    result[(type, relative)] = count;
        }

        return result;
    }

    private static Dictionary<(string Type, string File), int> ReadAllow(string path)
    {
        Assert.True(File.Exists(path),
            $"{AllowFileName} is missing — restore it from git rather than regenerating it: a "
            + "regenerated file would silently bless whatever is in the tree, which is the one thing "
            + "a ratchet must never do.");

        var result = new Dictionary<(string Type, string File), int>();
        foreach (var line in File.ReadAllLines(path).Select(l => l.Trim())
                     .Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            Assert.True(parts.Length == 3 && MessageTypes.Contains(parts[0]) && int.TryParse(parts[2], out _),
                $"{AllowFileName}: malformed line '{line}' — expected '<Type>\\t<file>\\t<count>' with "
                + $"<Type> one of {string.Join(", ", MessageTypes)}.");
            result[(parts[0], parts[1])] = int.Parse(parts[2]);
        }

        return result;
    }
}
