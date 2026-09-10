using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The instrument <see href="/Doc/Architecture/ContentRoute503">The /api/content 503</see> names
/// must actually be emitted, by the code that page points at, ON ONE LINE.
///
/// <para><b>Why a guard and not a comment.</b> That page's whole value is one sentence — *"grep for
/// <c>Reading content collection config from</c> and read the <c>Queue(</c> on the same line"* —
/// and every word of it is a claim about code in three different assemblies. The string is not a
/// log message at all: it is the MESSAGE of the <see cref="HubUnreachableException"/> that
/// <see cref="ReadBudget"/> builds, carried into the log only as the exception detail of a warning
/// raised in another repository. Its <c>Reader:</c> half comes from
/// <c>MessageHub.GetPendingRequestDiagnostics</c>. Nothing in a normal build ties any of that to
/// the sentence on the page, so all three can drift and the page keeps telling an incident reader
/// to look for text that no longer exists — which is precisely how the field rename recorded on
/// that page (<c>exec=</c> → <c>drainsInFlight=</c>, #3593) reached production undetected.</para>
///
/// <para><b>The <c>what</c> is READ from the call site, never restated.</b> A guard that hard-coded
/// <c>"content collection config"</c> would go green through the exact rename it exists to catch,
/// so the argument is scanned out of <c>ContentFileResolver.Resolve</c>'s own
/// <c>FailIfNoFirstEmission(…)</c> call and fed to the real budget.</para>
///
/// <para><b>The single-line claim is the one with teeth.</b> Both readers of that line — the
/// break-glass <c>grep -o "Queue(buffer=[0-9]*"</c> and the per-physical-line log projection
/// described in <see href="/Doc/Architecture/LogEntriesAreAQueryResult">Log Entries Are a Query
/// Result</see> — split on newlines. One <c>Environment.NewLine</c> in the snapshot and the
/// discriminator silently stops travelling with the sentence that names the read. Its sibling
/// <c>GetDisposalDiagnostics</c> IS multi-line, which is what makes the check here a real
/// discrimination rather than a property of every diagnostic string.</para>
/// </summary>
public class ContentRoute503DiscriminatorGuard(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The page whose instruction this guard keeps honest.</summary>
    private const string PagePath = "src/MeshWeaver.Documentation/Data/Architecture/ContentRoute503.md";

    /// <summary>The call site the page's grep string is derived from.</summary>
    private const string CallSitePath = "src/MeshWeaver.ContentCollections/ContentFileResolver.cs";

    /// <summary>The Cause-A / Cause-B field, as the page quotes it.</summary>
    private const string QueueField = "Queue(buffer=";

    /// <summary>
    /// The clause that decides Cause C, and the one ContentRoute503 says to read FIRST. It must ride
    /// the same physical line as <see cref="QueueField"/>, or the two halves of the verdict cannot
    /// be fetched by one filter.
    /// </summary>
    private const string TargetClause = "Target:";

    /// <summary>A target that exists nowhere — this read is never meant to be answered.</summary>
    private const string AbsentTarget = "TestData/ContentProbe";

    [Fact]
    public void ThePageTellsYouToGrepForWhatTheCodeActuallyEmits()
    {
        var page = ReadPage();
        var what = WhatFromTheCallSite();
        var greppable = $"Reading {what} from";

        page.Should().Contain(greppable,
            $"ContentRoute503 tells an incident reader to grep for this; the call site in "
            + $"{CallSitePath} passes what: \"{what}\", so the framework emits \"{greppable} '…'\". "
            + "One of the two moved — fix the page, or the call site, so they agree.");

        var failure = LapsedBudget(GetHost(), what);
        failure.Message.Should().Contain(greppable,
            "the string the page names must be the one ReadBudget.Unreachable actually builds");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for the assertion above. A message built with a DIFFERENT <c>what</c>
    /// must NOT satisfy the page's grep string — otherwise the assertion would pass on any lapsed
    /// budget anywhere in the framework and would say nothing about the content route.
    /// </summary>
    [Fact]
    public void TheGrepStringTracksTheCallSitesOwnArgument_AndNotJustAnyLapsedBudget()
    {
        var greppable = $"Reading {WhatFromTheCallSite()} from";

        LapsedBudget(GetHost(), "node content").Message.Should().NotContain(greppable,
            "if a lapsed budget for some OTHER read also matched, the previous test would be "
            + "asserting nothing about ContentFileResolver — and a rename of its `what` argument "
            + "would leave the page's instruction stale with the guard still green");
    }

    [Fact]
    public void TheReaderSnapshotRidesOnTheSameLine_SoTheQueueFieldIsGreppableWithIt()
    {
        var what = WhatFromTheCallSite();
        var message = LapsedBudget(GetHost(), what).Message;

        message.Should().Contain(QueueField,
            "ContentRoute503's discriminator IS this field; it reaches the message only because "
            + "ReadBudget.Unreachable asks the reader hub for GetPendingRequestDiagnostics()");
        message.Should().Contain(TargetClause,
            "the TARGET clause is the OTHER half of the verdict — it is what separates Cause C "
            + "(the owner had not finished STARTING) from Cause A, which the reader clause cannot "
            + "see because a start-up leaves the reader idle (#3931). ContentRoute503 says to read "
            + "it FIRST, so it has to be on the line you fetch");
        // 🚨 CONTROL for the assertion above: the two clauses come from DIFFERENT code — the reader
        // snapshot alone carries no Target:, so finding both on one line is a fact about the
        // composed message rather than a substring that was always going to be there.
        GetHost().GetPendingRequestDiagnostics().Should().NotContain(TargetClause,
            "GetPendingRequestDiagnostics is the READER half only; the Target clause is "
            + "ReadBudget.DescribeTarget's. If the snapshot started carrying it, the assertion "
            + "above would pass without ReadBudget composing the two, and Cause C could go "
            + "undetectable while this guard stayed green");
        Assert.True(
            !message.Contains('\n') && !message.Contains('\r'),
            "🚨 The /api/content 503 discriminator must stay on ONE physical line. "
            + "ContentRoute503 says \"read the `Queue(` on the same line\", the break-glass form is "
            + "`grep -o \"Queue(buffer=[0-9]*\"`, and the log projection makes each physical line "
            + "its own Hosting/LogEntry node — all three split here. Keep "
            + "MessageHub.GetPendingRequestDiagnostics single-line (GetDisposalDiagnostics is the "
            + "multi-line one).\n\nMessage was:\n" + message);
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL for the newline check. <c>GetDisposalDiagnostics</c> is the deliberately
    /// MULTI-line sibling of the single-line snapshot, so a "contains no newline" predicate that
    /// could never fail would fail here. Without this, a bug that made every diagnostic a single
    /// empty string would read as a pass.
    /// </summary>
    [Fact]
    public void TheNewlineCheckCanFail_BecauseTheDisposalSnapshotIsDeliberatelyMultiLine()
    {
        var multiLine = GetHost().GetDisposalDiagnostics();

        Assert.True(multiLine.Contains('\n') || multiLine.Contains('\r'),
            "GetDisposalDiagnostics walks the hosted-hub tree one line per hub, so it MUST contain "
            + "a newline. If it no longer does, the single-line assertion in "
            + nameof(TheReaderSnapshotRidesOnTheSameLine_SoTheQueueFieldIsGreppableWithIt)
            + " has lost its power to discriminate and this pair must be re-based on something that "
            + "still separates the two shapes.\n\nSnapshot was:\n" + multiLine);
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL for the <c>Queue(buffer=</c> assertion: the field is contributed by the
    /// READER HUB, not baked into the format string. With no reader there is no snapshot, and the
    /// message says so instead of inventing one — which is also the shape a reader will meet when
    /// the budget lapses on a hub that has already been torn down.
    /// </summary>
    [Fact]
    public void WithNoReaderHubTheDiscriminatorIsAbsent_AndSaysSoRatherThanReadingZero()
    {
        var message = LapsedBudget(reader: null, WhatFromTheCallSite()).Message;

        message.Should().NotContain(QueueField,
            "with no reader hub there is no queue to report; if this field appeared anyway it "
            + "would be a constant in the format string, and Cause A would read identically to a "
            + "read that never had a reader at all");
        message.Should().Contain("<no reader hub>",
            "an absent snapshot must be stated, never rendered as an empty or zeroed one");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives <see cref="ReadBudget.FailIfNoFirstEmission{T}"/> to its bound in VIRTUAL time
    /// against a source that never answers — the production shape, at no wall-clock cost. The
    /// budget is <see cref="ReadBudget.Default"/> rather than a literal, so this cannot drift from
    /// the bound the route actually uses.
    /// </summary>
    private static HubUnreachableException LapsedBudget(IMessageHub? reader, string what)
    {
        var scheduler = new TestScheduler();
        Exception? captured = null;
        using (Observable.Never<int>()
                   .FailIfNoFirstEmission(reader, AbsentTarget, what, ReadBudget.Default, scheduler)
                   .Subscribe(_ => { }, ex => captured = ex))
        {
            scheduler.AdvanceBy(ReadBudget.Default.Ticks);
        }

        return Assert.IsType<HubUnreachableException>(captured);
    }

    /// <summary>
    /// The <c>what</c> argument of <c>ContentFileResolver</c>'s own
    /// <c>FailIfNoFirstEmission(…)</c> call — READ, never restated. An empty or ambiguous scan is a
    /// broken guard, not a clean one, so it throws rather than defaulting.
    /// </summary>
    private static string WhatFromTheCallSite()
    {
        var file = Path.Combine(SourceScan.FindRepoRoot(), CallSitePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file),
            $"🚨 {CallSitePath} is gone — this guard's subject moved and the guard did not. "
            + "Point it at the call site that now issues the collection-config read, or delete "
            + "ContentRoute503's grep instruction along with it.");

        var source = File.ReadAllText(file);
        var call = source.IndexOf("FailIfNoFirstEmission(", StringComparison.Ordinal);
        Assert.True(call >= 0,
            $"🚨 {CallSitePath} no longer bounds the collection-config read with "
            + "FailIfNoFirstEmission — ContentRoute503's whole elimination rests on that bound "
            + "being the only 10 s on the route. Re-read the page before removing it.");

        var window = source.Substring(call, Math.Min(400, source.Length - call));
        var literals = Regex.Matches(window, "\"(?<value>[^\"\\\\]*)\"")
            .Select(m => m.Groups["value"].Value)
            .Where(v => v.Length > 0)
            .ToArray();
        Assert.True(literals.Length == 1,
            $"🚨 Expected exactly ONE string literal in {CallSitePath}'s FailIfNoFirstEmission call "
            + $"(its `what`), found {literals.Length}: [{string.Join(", ", literals.Select(l => $"\"{l}\""))}]. "
            + "The scan cannot tell which one names the read, so it refuses to guess — reshape the "
            + "call or teach the guard the new shape.");

        return literals[0];
    }

    private static string ReadPage()
    {
        var file = Path.Combine(SourceScan.FindRepoRoot(), PagePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file),
            $"🚨 {PagePath} is gone. This guard exists to keep that page's instruction executable; "
            + "if the page moved, move the guard with it.");
        return File.ReadAllText(file);
    }
}
