#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 <b>content/ai IS THE ONE MASTER for the built-in agents and skills</b> — and edits to it
/// must be DELIBERATE, never drift. Originally issue #1627, reframed 2026-08-28.
///
/// <para><b>History, because the ledger's field names still show it:</b> the roster used to exist
/// twice — here AND as the <c>Agent/</c> + <c>Skill/</c> packages in the private
/// <c>Systemorph/MeshWeaver.Plugins</c> repo. The copies drifted in both directions for weeks.
/// On 2026-08-28 the duplicate packages were consolidated away (their genuine deltas adopted
/// here first), so <c>content/ai</c> — served on every portal by the AI engine's
/// <c>BuiltIn*Provider</c>s — is the single master, and every ledger entry is
/// <c>PackAbsent</c> by construction.</para>
///
/// <para><b>What the guard still does, and why it stays:</b> it pins the ROSTER and every file's
/// content hash (<c>TestData/AiContentPackSync.json</c>), so adding, deleting or editing a
/// built-in agent/skill is a recorded decision rather than an accident — these are the DEFAULT
/// CHAT INSTRUCTIONS of every deployment. And it refuses any file <b>in this section</b> carrying
/// an identifier that must never appear in public: the retired pack was private, this repo is
/// public, and the sanitised placeholders are permanent. Everything below is scoped to
/// <c>content/ai/**</c> — it is not a repository-wide secret scan.</para>
///
/// <para><b>The forbidden identifiers are stored as HASHES</b>, and a hit is reported by
/// <c>file:line</c> only — never by echoing the token. A deny-list that spells the name out, or a
/// failure message that prints it, would publish in this repository (and in its public CI log)
/// exactly what it exists to keep out.</para>
/// </summary>
public class AiContentPackDriftTest
{
    // ── The pinned ledger ──────────────────────────────────────────────────────

    private sealed record PackAttestation(string? Repo, string? Commit, string? Observed);

    private sealed record ForbiddenIdentifier(string Sha256, string Reason);

    private sealed record LedgerEntry(
        string File,
        string Core,
        string? Pack,
        string State,
        string? Note,
        string? SanitisedToken);

    private sealed record PackSyncLedger(
        PackAttestation? Pack,
        IReadOnlyList<ForbiddenIdentifier>? ForbiddenIdentifiers,
        IReadOnlyList<LedgerEntry>? Files);

    /// <summary>The states an entry may declare. <c>SanitisedOnly</c> is load-bearing: it is the
    /// record that a file differs from the master ONLY by a placeholder, and must stay that way.</summary>
    private static readonly string[] KnownStates =
        ["InSync", "CoreAhead", "PackAhead", "SanitisedOnly", "PackAbsent"];

    private const string LedgerFileName = "AiContentPackSync.json";

    private static readonly JsonSerializerOptions LedgerJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static PackSyncLedger Ledger()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", LedgerFileName);
        System.IO.File.Exists(path).Should().BeTrue(
            "the pack-sync ledger must ship beside the test at {0} — without it this guard checks "
            + "nothing, and a guard that cannot find its input must be RED, never green", path);

        var ledger = JsonSerializer.Deserialize<PackSyncLedger>(
            System.IO.File.ReadAllText(path), LedgerJson);
        ledger.Should().NotBeNull("the ledger must parse");
        ledger!.Files.Should().NotBeEmpty("the ledger must pin at least one file");
        return ledger;
    }

    // ── The content section ────────────────────────────────────────────────────

    /// <summary>Every authored file in the AI content section, as <c>Section/File.md</c> → text.</summary>
    private static IReadOnlyDictionary<string, string> Section()
    {
        var root = AiContentLocator.SectionRoot();
        root.Should().NotBeNull(
            "the AI content section (content/ai, or the AiContent copy beside the assembly) must be "
            + "resolvable from the test run — this guard has no meaningful 'skip'");

        var files = Directory
            .EnumerateFiles(root!, "*.md", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root!, f).Replace('\\', '/'),
                System.IO.File.ReadAllText,
                StringComparer.Ordinal);

        files.Should().NotBeEmpty("the section must ship authored files");
        return files;
    }

    /// <summary>
    /// The hash the ledger pins. Text, not bytes: a BOM, a CRLF checkout or a trailing blank line is
    /// not drift, and a guard that fired on those would be turned off within a week.
    /// </summary>
    internal static string ContentHash(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        if (normalized.StartsWith('\uFEFF'))
            normalized = normalized[1..];
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string TokenHash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ── 1. The roster ──────────────────────────────────────────────────────────

    /// <summary>
    /// Adding or deleting an agent/skill file is exactly the moment someone has to decide what
    /// happens to the master, so the roster is pinned rather than discovered. A file that appears
    /// here and never in the pack is how the two copies started diverging in the first place.
    /// </summary>
    [Fact]
    public void TheRoster_MatchesTheLedger()
    {
        var onDisk = Section().Keys.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var pinned = Ledger().Files!.Select(f => f.File)
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();

        onDisk.Should().Equal(pinned,
            "every file under content/ai must have a ledger entry and vice versa. Added: [{0}]. "
            + "Removed: [{1}]. Add or delete the entry in test/MeshWeaver.AI.Test/TestData/{2} — "
            + "this roster is the built-in default of every deployment, so it changes on purpose.",
            string.Join(", ", onDisk.Except(pinned, StringComparer.Ordinal)),
            string.Join(", ", pinned.Except(onDisk, StringComparer.Ordinal)),
            LedgerFileName);
    }

    // ── 2. Divergence from the pinned reconciliation point ─────────────────────

    /// <summary>
    /// 🚨 THE DRIFT DETECTOR. The pack half cannot be read from this repo's CI — it is private and
    /// this repo holds no credential for it — so what is enforceable from here is that a core file
    /// never moves away from the recorded reconciliation point <i>unnoticed</i>. Edit an agent's
    /// instructions and this test goes red, printing the new hash: updating the entry is where you
    /// record whether the master was updated too, or why it deliberately was not.
    /// </summary>
    [Fact]
    public void EveryFile_StillHashesToItsPinnedReconciliationPoint()
    {
        var section = Section();
        var drifted = Ledger().Files!
            .Where(e => section.TryGetValue(e.File, out var text) && ContentHash(text) != e.Core)
            // The NEW hash is printed in FULL, because re-pinning the entry means pasting it. A
            // truncated hash would turn a one-paste fix into "go and compute it yourself".
            .Select(e => $"{e.File}: pinned {e.Core[..12]}… → re-pin \"core\" to "
                + $"\"{ContentHash(section[e.File])}\"")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        drifted.Should().BeEmpty(
            "content/ai is the ONE master for the built-in agents and skills — what every portal "
            + "serves as its default chat instructions — so an edit must be a recorded decision, "
            + "not an accident. Re-pin the entry in test/MeshWeaver.AI.Test/TestData/{0} (paste "
            + "the new hash and say in the note what changed). Drifted: {1}",
            LedgerFileName, string.Join(" | ", drifted));
    }

    /// <summary>Every entry must declare a known state, and the state must agree with the
    /// attestation: a pack hash is absent exactly when there is nothing to compare against.</summary>
    [Fact]
    public void EveryEntry_DeclaresACoherentState()
    {
        var incoherent = Ledger().Files!
            .Select(e => (e.File, Problem: StateProblem(e)))
            .Where(x => x.Problem is not null)
            .Select(x => $"{x.File}: {x.Problem}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        incoherent.Should().BeEmpty(
            "a ledger entry whose state contradicts its own hashes documents nothing. Problems: {0}",
            string.Join(" | ", incoherent));
    }

    private static string? StateProblem(LedgerEntry e)
    {
        if (!KnownStates.Contains(e.State, StringComparer.Ordinal))
            return $"unknown state '{e.State}' (expected one of {string.Join("/", KnownStates)})";
        if (e.State == "PackAbsent")
            return e.Pack is null ? null : "PackAbsent but a pack hash is recorded";
        if (e.Pack is null)
            return $"{e.State} but no pack hash is recorded";
        if (e.State == "InSync")
            return e.Pack == e.Core ? null : "InSync but the two hashes differ";
        if (e.Pack == e.Core)
            return $"{e.State} but the two hashes are identical";
        return e.Note is null ? $"{e.State} without a note saying what diverged" : null;
    }

    // ── 3. Sanitisation — the divergence that must NEVER be reconciled ─────────

    /// <summary>
    /// 🚨 The public/private asymmetry, pinned. A file the ledger marks as sanitised must still carry
    /// its placeholder, and must still be recorded as diverging from the master — because the day it
    /// reads <c>InSync</c> is the day someone pasted the master's text back in, customer name and all.
    /// </summary>
    [Fact]
    public void SanitisedFiles_KeepTheirPlaceholder_AndStayDivergedOnPurpose()
    {
        var section = Section();
        var sanitised = Ledger().Files!.Where(e => e.SanitisedToken is not null).ToArray();

        sanitised.Should().NotBeEmpty(
            "at least one file is sanitised on purpose (the example namespace in the commenting "
            + "reference) — if this list empties, the rule below stopped being checked");

        foreach (var entry in sanitised)
        {
            section.TryGetValue(entry.File, out var text).Should().BeTrue(
                "{0} is pinned as sanitised but is not in the section", entry.File);
            text.Should().Contain(entry.SanitisedToken!,
                "{0} must keep its sanitised placeholder '{1}' — this repo is PUBLIC and the pack "
                + "master is PRIVATE, so this divergence is permanent, never something to reconcile",
                entry.File, entry.SanitisedToken!);
            // With the pack retired every entry is PackAbsent; the placeholder rule above is the
            // half that outlives the pack, and SanitisedOnly remains valid for history.
            entry.State.Should().BeOneOf(["SanitisedOnly", "PackAbsent"],
                "{0} carries a sanitised placeholder — its entry must not claim the un-sanitised "
                + "text is fine", entry.File);
        }
    }

    // ── 4. Nothing in the mirrored content section may name a customer ────────

    /// <summary>
    /// 🚨 THE LEAK GUARD. Reported as <c>file:line</c> and nothing else — echoing the token would
    /// publish it in the CI log of a public repository, which is the thing being prevented.
    ///
    /// <para><b>Scope, stated precisely because the gap matters:</b> this scans
    /// <c>content/ai/**</c> and nothing else. That is the surface this guard is about — the tree
    /// that MIRRORS the private pack, where a copy-the-master-over is the way a customer name
    /// gets in. It is NOT a repository-wide secret scan, and must not be read as one; a name
    /// pasted into a `.cs` file elsewhere is not covered here.</para>
    /// </summary>
    [Fact]
    public void NoForbiddenIdentifier_AppearsInTheContentSection()
    {
        var ledger = Ledger();
        var hashes = ForbiddenHashes(ledger);

        hashes.Should().NotBeEmpty(
            "an empty deny-list makes the scan below vacuous — it would pass over any content at all");

        var hits = Section()
            .SelectMany(kv => Hits(kv.Value, hashes).Select(line => $"{kv.Key}:{line}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        hits.Should().BeEmpty(
            "Systemorph/MeshWeaver.Plugins is PRIVATE and this repository is PUBLIC. A customer or "
            + "engagement identifier reaching content/ai — most easily by copying the pack master's "
            + "text over — publishes it. Replace it with the placeholder the rest of the section "
            + "uses. (The token is deliberately not printed. Sites: {0})",
            string.Join(", ", hits));
    }

    private static IReadOnlySet<string> ForbiddenHashes(PackSyncLedger ledger)
        => (ledger.ForbiddenIdentifiers ?? [])
            .Select(f => f.Sha256.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The 1-based line numbers on which some hashed identifier appears. Candidates are built so the
    /// same name is caught however it is written: as one word (<c>AcmeCorp</c>), spaced
    /// (<c>Acme Corp</c>), slashed in a path (<c>@/AcmeCorp/Docs</c>) or embedded in a longer
    /// camel-cased identifier (<c>MyAcmeCorpReport</c>). It is a word-level scan, not a substring
    /// scan: a name glued into a lowercase run (<c>myacmecorpreport</c>) is not caught.
    /// </summary>
    internal static IReadOnlyList<int> Hits(string text, params string[] forbiddenHashes)
        => Hits(text, forbiddenHashes.ToHashSet(StringComparer.Ordinal));

    /// <inheritdoc cref="Hits(string, string[])"/>
    internal static IReadOnlyList<int> Hits(string text, IReadOnlySet<string> forbiddenHashes)
    {
        if (forbiddenHashes.Count == 0)
            return [];

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var hits = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (Candidates(lines[i]).Any(c => forbiddenHashes.Contains(TokenHash(c))))
                hits.Add(i + 1);
        return hits;
    }

    /// <summary>How many adjacent atoms a candidate may span. A name longer than this many words
    /// would not be caught; six covers every company-name shape seen so far and keeps the scan at
    /// tens of milliseconds over the whole section.</summary>
    private const int MaxAtomSpan = 6;

    /// <summary>
    /// Every contiguous run of up to <see cref="MaxAtomSpan"/> atoms, glued together and lowercased.
    /// Atoms are word-parts: an alphanumeric run split at its camel-case boundaries. Gluing across
    /// atoms is what makes <c>AcmeCorp</c>, <c>Acme Corp</c>, <c>acme-corp</c>, <c>@/AcmeCorp/Docs</c>
    /// and <c>MyAcmeCorpReport</c> all produce the same candidate.
    /// </summary>
    private static IEnumerable<string> Candidates(string line)
    {
        var atoms = Atoms(line);
        for (var start = 0; start < atoms.Count; start++)
        {
            var builder = new StringBuilder();
            var last = Math.Min(atoms.Count - 1, start + MaxAtomSpan - 1);
            for (var end = start; end <= last; end++)
            {
                builder.Append(atoms[end]);
                yield return builder.ToString();
            }
        }
    }

    /// <summary>The line's word-parts, in order: alphanumeric runs split at camel-case boundaries,
    /// lowercased. Everything else (punctuation, slashes, whitespace) is a separator and is dropped
    /// — which is exactly why a name reads the same however it was punctuated.</summary>
    private static List<string> Atoms(string line)
    {
        var atoms = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
                return;
            atoms.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }

        foreach (var ch in line)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                Flush();
                continue;
            }
            if (char.IsUpper(ch))
                Flush();
            current.Append(ch);
        }
        Flush();
        return atoms;
    }

    // ── 5. THE CONTROLS ────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The leak guard passes trivially on clean content, so on its own it is not evidence that it
    /// works. This drives the SAME scanner over the exact shape that caused #1627 — the pack master's
    /// example path with a company name in the namespace — and requires a hit, in every spelling.
    /// The name used here is invented, for the same reason the real list is hashed.
    /// </summary>
    [Theory]
    [InlineData("- Pass the canonical node path (`@/GlobalReinsuranceAg/AIConsulting/FinalReport`).")]
    [InlineData("@@(\"area:OgCard/GlobalReinsuranceAg/EslProposalQa\")")]
    [InlineData("const string nested = \"https://portal.example.org/GlobalReinsuranceAg/Proposal\";")]
    [InlineData("Mirrors the **Global Reinsurance Ag** space into the repository.")]
    [InlineData("the report prepared for Global-Reinsurance-Ag last quarter")]
    [InlineData("a MyGlobalReinsuranceAgReport node under the customer partition")]
    public void TheLeakGuard_FiresOnACustomerName(string line)
    {
        Hits(line, TokenHash("globalreinsuranceag")).Should().Equal([1],
            "the scanner must catch the identifier however it is spelled — one word, spaced, "
            + "hyphenated, inside a path segment or embedded in a longer camel-cased name. Line: {0}",
            line);
    }

    /// <summary>The other direction: the scanner must not fire on the placeholder the section
    /// actually uses, or on ordinary prose. A guard that cries wolf gets deleted.</summary>
    [Theory]
    [InlineData("- Pass the canonical node path (`@/Acme/AIConsulting/FinalReport`).")]
    [InlineData("Global reinsurance is a market, not a customer.")]
    [InlineData("")]
    public void TheLeakGuard_StaysQuietOnPlaceholdersAndProse(string line)
        => Hits(line, TokenHash("globalreinsuranceag")).Should().BeEmpty(
            "false positives are how a leak guard gets switched off. Line: {0}", line);

    /// <summary>
    /// 🚨 The sanitisation control. Re-syncing the sanitised file from the private master is the
    /// exact accident #1627 warns about; this proves BOTH pins catch it — the hash stops matching
    /// AND the placeholder disappears — so it cannot slip through by updating one of them.
    /// </summary>
    [Fact]
    public void ReSyncingASanitisedFileFromTheMaster_TripsBothPins()
    {
        var entry = Ledger().Files!.Single(f => f.SanitisedToken is not null);
        var live = Section()[entry.File];

        ContentHash(live).Should().Be(entry.Core, "the live file is at its pinned point to begin with");
        live.Should().Contain(entry.SanitisedToken!);

        // What "copy the master over" would produce: the placeholder replaced by a company name.
        var resynced = live.Replace(entry.SanitisedToken!, "GlobalReinsuranceAg", StringComparison.Ordinal);

        ContentHash(resynced).Should().NotBe(entry.Core,
            "the pinned hash must stop matching, so the drift detector fires");
        resynced.Contains(entry.SanitisedToken!, StringComparison.Ordinal).Should().BeFalse(
            "the placeholder is gone, so the sanitisation pin fires too");
        Hits(resynced, TokenHash("globalreinsuranceag")).Should().NotBeEmpty(
            "and the leak guard names the line");
    }
}
