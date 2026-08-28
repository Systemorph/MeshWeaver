using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Instance registration is IRREVERSIBLE twice over: the registry takes the instance id
/// permanently, and it issues the instance key EXACTLY ONCE. So every local precondition for
/// STORING that key must be satisfied BEFORE the key is requested.
///
/// <para>🚨 <b>Why a source guard and not a test.</b> The failure cannot be reproduced without
/// causing it — there is no way to observe "the id was burned" except by burning one against a
/// real registry, and <c>InstanceRegistrationClient</c> is sealed with a non-virtual
/// <c>Register</c>, so the call cannot be faked either. What IS checkable, cheaply and exactly, is
/// the ordering that makes the failure impossible.</para>
///
/// <para>This is not hypothetical (#2585): resolving <c>IProviderKeyProtector</c> with
/// <c>GetRequiredService</c> INSIDE the success callback meant a mesh without that service
/// registered its instance, received the one-time key, threw while storing it, and left the
/// operator with an id that can never be registered again and a key nobody will ever see. The
/// error even blamed the bootstrap key, which had worked perfectly.</para>
/// </summary>
public class IrreversibleRegistrationOrderingGuard
{
    private const string File_ = "src/MeshWeaver.PluginCatalog/InstanceAutoRegistrationService.cs";

    [Fact]
    public void Everything_needed_to_store_the_key_is_resolved_before_it_is_requested()
    {
        var root = SourceScan.FindRepoRoot();
        var path = Path.Combine(root, File_);
        Assert.True(File.Exists(path), $"guarded file moved — update this guard: {File_}");

        var whole = SourceScan.MaskCommentsAndStrings(File.ReadAllText(path));

        // 🚨 Scope to EnsureRegistered's body. The file resolves the protector in ANOTHER method
        // too, and a whole-file search finds that one and reports the ordering as fine no matter
        // what EnsureRegistered does — a guard that passes by looking at the wrong occurrence,
        // which is worse than no guard. (Caught while red-proofing this one.)
        var start = whole.IndexOf("EnsureRegistered(PluginCatalogOptions", StringComparison.Ordinal);
        Assert.True(start >= 0, "EnsureRegistered(PluginCatalogOptions …) not found — update this guard");
        var next = whole.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var masked = next > start ? whole[start..next] : whole[start..];

        var register = Regex.Match(masked, @"\bclient\s*\.\s*Register\s*\(");
        Assert.True(
            register.Success,
            "no `client.Register(` call found — either the registration moved or this guard has "
            + "stopped watching the thing it was written for");

        var protectorResolve = Regex.Match(masked, @"GetService<\s*IProviderKeyProtector\s*>\s*\(");
        Assert.True(
            protectorResolve.Success,
            "IProviderKeyProtector is no longer resolved with GetService in this file. If the "
            + "credential no longer needs a protector, delete this guard deliberately; if it moved "
            + "to GetRequiredService inside the success path, that is exactly the #2585 defect.");

        Assert.True(
            protectorResolve.Index < register.Index,
            "IProviderKeyProtector is resolved AFTER client.Register — the ordering #2585 fixed. "
            + "Registration spends the instance id permanently and yields the key exactly once, so "
            + "a precondition checked afterwards converts a missing service into a burned id and a "
            + "lost key that no retry can recover. Resolve it (and anything else the credential "
            + "write needs) before the register call, and refuse to register when it is absent.");

        // GetRequiredService for the protector anywhere in this file re-arms the original defect:
        // it throws instead of letting the caller decline to register.
        Assert.DoesNotMatch(
            new Regex(@"GetRequiredService<\s*IProviderKeyProtector\s*>"),
            masked);
    }
}
