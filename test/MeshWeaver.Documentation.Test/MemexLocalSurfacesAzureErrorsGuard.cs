#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A fatal step must never discard the error that explains it.</b>
///
/// <para><c>image_pull_acr</c> ran <c>az acr login -n meshweaver -o none <b>2>/dev/null</b></c> and,
/// on failure, said <i>"run 'az login' first"</i>. That is the one remedy which cannot help the
/// failure people actually hit: an account that IS signed in and simply holds no role on the
/// registry. Azure had answered precisely —</para>
///
/// <code>
/// (AuthorizationFailed) The client 'user@example.com' … does not have authorization to perform
/// action 'Microsoft.Resources/subscriptions/resources/read' over scope '/subscriptions/…'
/// </code>
///
/// <para>— and the redirect to <c>/dev/null</c> threw that sentence away. Reported 2026-08-31 by a
/// developer who signed out, signed back in and switched tenant before asking, because the tool had
/// told them to. The role was missing the whole time.</para>
///
/// <para><b>Why a guard and not just a fix.</b> The suppression is one character sequence, easy to
/// reintroduce whenever someone finds a command chatty — and the cost is not a worse message but a
/// WRONG one, sending the reader after a remedy the tool has effectively ruled out. So the rule is
/// asserted where it can be re-broken.</para>
/// </summary>
public class MemexLocalSurfacesAzureErrorsGuard
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static readonly string ScriptPath =
        Path.Combine("deploy", "homebrew", "bin", "memex-local");

    private static string Script() =>
        File.ReadAllText(Path.Combine(RepoRoot(), ScriptPath));

    /// <summary>
    /// Every line that invokes the Azure CLI and sends its stderr to <c>/dev/null</c>.
    ///
    /// <para>Scoped to <c>az</c> deliberately. Discarding stderr is legitimate for a PROBE whose
    /// failure is expected and handled — <c>docker image inspect</c> asking whether an image is
    /// present — and this guard must not police those. Azure's CLI is different in kind: its
    /// failures are configuration and entitlement problems whose only readable statement is the
    /// message itself.</para>
    /// </summary>
    private static IEnumerable<string> AzureCallsThatDiscardStderr(string script) =>
        script.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith('#'))
            .Where(line => Regex.IsMatch(line, @"(^|\s|\()az\s"))
            .Where(line => line.Contains("2>/dev/null", StringComparison.Ordinal)
                        || line.Contains("2> /dev/null", StringComparison.Ordinal));

    [Fact]
    public void TheAcrLogin_NeverDiscardsAzuresOwnError()
    {
        var offenders = AzureCallsThatDiscardStderr(Script()).ToList();

        Assert.True(offenders.Count == 0,
            $"{ScriptPath} sends the Azure CLI's stderr to /dev/null on {offenders.Count} line(s), "
            + "so the only statement of what is actually wrong — a missing role, an expired "
            + "session, the wrong subscription — is destroyed before anyone can read it:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The remedy must cover the case the old text got wrong. "Run 'az login'" is right only when
    /// the caller is signed OUT; the reported failure was a signed-in account with no role, and a
    /// message that names only the sign-in leaves that reader with nothing to do.
    /// </summary>
    [Fact]
    public void TheAcrFailure_NamesTheRoleAndNotOnlyTheSignIn()
    {
        var script = Script();

        var start = script.IndexOf("image_pull_acr()", StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"{ScriptPath} no longer defines image_pull_acr — this guard would silently stop "
            + "covering the ACR failure path.");

        // The function body, to its closing brace at column 0.
        var end = script.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not delimit image_pull_acr in {ScriptPath}.");
        var body = script[start..end];

        Assert.True(body.Contains("AcrPull", StringComparison.Ordinal),
            "The ACR login failure does not name the AcrPull role. An account that is signed in "
            + "but unauthorized is the common case, and the message must say what to ask for.");

        Assert.True(body.Contains("--build", StringComparison.Ordinal),
            "The ACR login failure does not mention '--build'. A developer with no registry access "
            + "at all still has a way to run: build the image locally instead of pulling it.");
    }
}
