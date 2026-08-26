using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Every teardown-time failure <c>MessageService</c> produces must carry a CLASSIFICATION — either
/// <c>Failed(message, ErrorType)</c>, or <c>FailedAndNacked(message)</c> when the site already
/// answered the sender itself.
///
/// <para>🚨 <b>Why a source guard rather than a behavioural test.</b> This file's own
/// <c>DeletedAddressMessage</c> comment states the failure mode: "a drift here is SILENT: it does
/// not fail to compile, it quietly re-opens #1029 at whichever site was missed. Which is exactly
/// what happened — the fix landed only on the abandoned-delivery site, and the accepted-then-faulted
/// site kept answering the transient ErrorType.ShuttingDown with the raw exception text." The bug
/// class is a site that LOOKS right and is never exercised by the test that covers its sibling.
/// Nothing about it is racy, so pinning it racily would trade a silent bug for a flaky one.</para>
///
/// <para><b>What goes wrong when a classification is missing (#2350).</b> The reporting path reads
/// <c>delivery.GetFailureErrorType(ErrorType.Unavailable)</c> — so an unclassified failure reaches
/// the sender as <c>Unavailable</c> while its message still says "shutting down": a transient race
/// wearing an authoritative label. Every consumer written against the documented contract then
/// treats it as terminal — <c>SynchronizationStream</c>'s resubscribe latch, <c>MeshNodeStreamCache</c>'s
/// shutdown-drop handling, <c>PackageInstaller</c>'s retry — which is the precise outcome
/// <c>Failed(string, ErrorType)</c> exists to prevent, in its own words.</para>
///
/// <para>The guard is deliberately narrow: it only asks about failures whose MESSAGE mentions
/// shutting down / disposing. A genuine bug must stay terminal and unclassified is right for it.</para>
/// </summary>
public class TeardownFailureClassificationGuard
{
    /// <summary>Failure text that means "teardown", i.e. transient and retryable.</summary>
    private static readonly string[] TeardownWords = ["shutting down", "shut down", "disposing"];

    [Fact]
    public void EveryTeardownFailure_InMessageService_CarriesAClassification()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MeshWeaver.Messaging.Hub", "MessageService.cs"));

        // `Failed("...")` with a single STRING-LITERAL argument — the overload that records no
        // ErrorType. Two args (message, ErrorType) and FailedAndNacked(message) both classify, and
        // neither matches: the pattern requires the closing paren straight after the literal.
        var unclassified = Regex
            .Matches(source, """(?<!AndNacked)\bFailed\(\s*"([^"]*)"\s*\)""")
            .Select(m => m.Groups[1].Value)
            .Where(message => TeardownWords.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();

        Assert.True(unclassified.Length == 0,
            "MessageService produces a teardown failure with NO ErrorType. It will reach the sender "
            + "as ErrorType.Unavailable (the reporting fallback) while saying it is shutting down, so "
            + "consumers that ride out ShuttingDown will treat a recoverable race as terminal (#2350).\n"
            + "Use Failed(message, ErrorType.ShuttingDown), or FailedAndNacked(message) if the site "
            + "already answered the sender — see AnswerUnreleasableDelivery for the idiom.\n"
            + "Unclassified:\n  - " + string.Join("\n  - ", unclassified));
    }

    /// <summary>
    /// The interpolated form of the same mistake. Kept separate so the failure message can say which
    /// shape it found — an interpolated literal is easy to add without noticing it took the
    /// single-argument overload.
    /// </summary>
    [Fact]
    public void EveryInterpolatedTeardownFailure_InMessageService_CarriesAClassification()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MeshWeaver.Messaging.Hub", "MessageService.cs"));

        var unclassified = Regex
            .Matches(source, """(?<!AndNacked)\bFailed\(\s*\$"([^"]*)"\s*\)""")
            .Select(m => m.Groups[1].Value)
            .Where(message => TeardownWords.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();

        Assert.True(unclassified.Length == 0,
            "MessageService produces an interpolated teardown failure with NO ErrorType (#2350). "
            + "Use Failed(message, ErrorType.ShuttingDown) or FailedAndNacked(message).\n"
            + "Unclassified:\n  - " + string.Join("\n  - ", unclassified));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
