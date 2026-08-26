using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard (#2126): no call to <c>IProviderKeyProtector.Protect</c> may carry a fallback
/// that stores the secret unencrypted instead.
///
/// <para><b>What went wrong.</b> <c>Protect</c> used to return the plaintext unchanged when no
/// master key was configured, and four call sites wrote <c>protector?.Protect(x) ?? x</c> or
/// <c>protector is null ? x : protector.Protect(x)</c> on top of that. Both layers are a fallback,
/// and both are silent: an unconfigured deployment persisted raw credentials into node content with
/// nothing failing, nothing logged at the call site, and a green encryption test — because that test
/// configures a master key, so the degradation only ever happened where nobody was testing. It was
/// found in PRODUCTION on 2026-08-24 with a live OpenRouter key in cleartext in
/// <c>Provider/OpenRouter</c>, readable by anyone with read on that namespace.</para>
///
/// <para><b>Why a guard rather than care.</b> The fallback shapes are what a null-tolerant
/// <c>GetService&lt;T&gt;()</c> naturally invites — the compiler asks for them, and every one of them
/// reads as defensive rather than as a leak. There is no exception to see, no log line to grep, and
/// a stored plaintext key still WORKS (<c>Unprotect</c> passes untagged values through by design), so
/// nothing downstream ever notices. A reviewer would have to know the whole story to object; a text
/// scan just has to match a character.</para>
///
/// <para>🚨 The fix for a failure here is never an exemption and never a suppression. Resolve the
/// protector with <c>GetRequiredService</c> and call <c>Protect</c> unconditionally: it refuses
/// (throws, naming <c>Ai:KeyProtection:MasterKey</c>) when it cannot encrypt, and a credential that
/// cannot be encrypted is a credential that must not be stored. If — and only if — the caller owns a
/// structured, non-writing refusal to report instead of an exception (the boot seed's
/// <c>ProviderSeedOutcome.RefusedUnprotected</c>), ask <c>IMasterKeyProvider.GetMasterKey()</c>
/// FIRST and skip the write. That is a decision not to write; it is not a fallback, and it never
/// reaches <c>Protect</c>.</para>
///
/// <para><b>Deliberately not matched:</b> <c>Unprotect</c> in any shape — reads stay tolerant so an
/// instance already holding legacy plaintext keeps working after an upgrade. Fail on the way IN,
/// tolerate on the way OUT.</para>
/// </summary>
public class NoPlaintextCredentialFallbackGuard
{
    /// <summary>
    /// Everywhere a credential can be written: the framework and the portal host. Both are scanned
    /// because the leak class is not owned by one subsystem — a model provider's ApiKey, a GitHub
    /// PAT, the plugin registry's sync-token signing key, an installation's registry key and the
    /// Entra EA refresh token all go through the same protector.
    /// </summary>
    private static readonly string[] ScannedRoots = ["src", "memex"];

    /// <summary>
    /// A null-conditional <c>Protect</c>. There is no benign reason to write one: it can only exist
    /// to let a null protector fall through to something else, and that something else is the raw
    /// secret. Note <c>\.</c> means this never matches <c>Unprotect</c>.
    /// </summary>
    private static readonly Regex NullConditionalProtect = new(@"\?\.Protect\s*\(", RegexOptions.Compiled);

    /// <summary>Any <c>Protect</c> call at all — used both to find fallbacks and to prove the scan saw the real sites.</summary>
    private static readonly Regex AnyProtect = new(@"\.Protect\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The ternary / null-coalescing fallback sharing a line with a <c>Protect</c> call:
    /// <c>protector is null ? plaintext : protector.Protect(plaintext)</c> and
    /// <c>protector.Protect(x) ?? x</c>. Line-oriented on purpose — it is a ratchet over the shapes
    /// that actually shipped, not a parser; the null-conditional rule above catches the form a
    /// reflowed statement would most likely take.
    /// </summary>
    private static readonly Regex FallbackOnTheSameLine =
        new(@"(\?\?)|(is\s+not\s+null\s*\?)|(is\s+null\s*\?)", RegexOptions.Compiled);

    [Fact]
    public void NoProtectCallSite_FallsBackToStoringThePlaintext()
    {
        var root = SourceScan.FindRepoRoot();

        // 🚨 A guard must never pass on no evidence. SourceFiles() silently drops a root that does
        // not exist, so a renamed directory would turn this into a green check that scanned nothing.
        foreach (var scanned in ScannedRoots)
            Assert.True(Directory.Exists(Path.Combine(root, scanned)),
                $"Scanned root '{scanned}' does not exist — this guard would scan nothing and pass. "
                + "Update ScannedRoots to match the tree; never delete the root to make it green.");

        var offenders = new List<string>();
        var protectSites = 0;

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            // Mask comments and string literals first — this file's own remarks quote the exact
            // shapes being ratcheted, and so do the call sites' (they explain why the fallback was
            // removed). An unmasked scan would report its own documentation as the defect.
            var masked = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
            var lines = masked.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!AnyProtect.IsMatch(line))
                    continue;

                protectSites++;

                if (NullConditionalProtect.IsMatch(line) || FallbackOnTheSameLine.IsMatch(line))
                    offenders.Add($"{SourceScan.Relative(root, file)}:{i + 1}");
            }
        }

        // The scan must have SEEN the credential writers, or "zero offenders" says nothing about the
        // tree — it would just mean the masker or the regex stopped matching.
        Assert.True(protectSites >= 4,
            $"Only {protectSites} Protect(...) call sites were found across {string.Join(", ", ScannedRoots)} — "
            + "too few to be the real tree, so a pass here would mean nothing. Check the regex and the masker.");

        Assert.True(offenders.Count == 0,
            "A credential-protection call site carries a fallback that would store the secret "
            + "unencrypted. Resolve IProviderKeyProtector with GetRequiredService and call Protect "
            + "unconditionally — it refuses when it cannot encrypt, and a credential that cannot be "
            + "encrypted must not be stored:\n  " + string.Join("\n  ", offenders));
    }
}
