using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#1549 — a release failure must arrive as ITS OWN cause, not as a recurrence of somebody
/// else's.</b>
///
/// <para><b>Measured, not assumed.</b> <c>Admin/_LogIncident/19be79b3588b8152</c> on the control
/// instance, read 2026-09-19: <c>occurrences: 326</c>, <c>firstSeen 2026-08-14</c>,
/// <c>lastSeen 2026-09-17</c>, over 16 pods — ONE incident, whose ten retained samples carry FOUR
/// unrelated conditions:</para>
/// <list type="bullet">
///   <item><c>Update aborted: no initial state arrived for 'Hosting/InstanceAction' within 30s</c> —
///     a base-state stall, the cause the issue was closed on (#1990).</item>
///   <item><c>The release request for 'Crm/Contact' produced no answer within 180s</c> — a
///     non-terminating leg (#3510's shape).</item>
///   <item><c>MeshNode OwnerUnreachable at 'Store/Tier': … returned no verdict …</c> — a routing
///     no-verdict.</item>
///   <item><c>Cannot access a disposed object. Object name: 'MeshNodeStreamCache'.</c> — a host
///     teardown (#1540's family).</item>
/// </list>
///
/// <para><b>The mechanism.</b> All four went through ONE template —
/// <c>"[Recompile] Release request for {Path} failed: {Message}"</c> — with the entire reason inside
/// a structured parameter. So every close of this issue against one cause was followed by a
/// recurrence on a different one, and the ticket could never reach zero. Worse for the last row: an
/// incident's discriminating text is the FIRST body line of the burst, and
/// <c>Object name: 'MeshNodeStreamCache'</c> is on the SECOND — the only words that said which fault
/// it was never reached the identity at all.</para>
///
/// <para><b>What this pins, and what it deliberately does not.</b> It pins that distinct causes
/// produce distinct message TEMPLATES, because the template is what a reader greps and what a
/// ticket title carries. It does NOT assert a fingerprint: the identity is computed in
/// <c>MeshWeaver.Plugins</c> (<c>StructuralLogIncidentIdentity</c>), which core cannot reference —
/// asserting one here would pin a COPY of a rule that lives somewhere else, which is how a guard
/// comes to pass while its subject has moved.</para>
///
/// <para>🚨 <b>The negative control, watched red before this was committed:</b> collapse
/// <see cref="NodeTypeRecompileExtensions.LogReleaseRefusal"/> to the single pre-change template and
/// <see cref="EveryFailureClass_GetsATemplateOfItsOwn"/> fails with 1 distinct template for every
/// class, while <see cref="TwoUnrelatedCauses_ProduceTwoDifferentTemplates"/> fails on the
/// equality it exists to refuse. That is the state production was in.</para>
/// </summary>
public class ReleaseFailureClassIsInTheTemplateTest
{
    private const string Path = "Store/Tier";

    /// <summary>
    /// THE control. Two causes that are genuinely different defects — a node that does not exist
    /// (not a defect in the release path at all) and a host tearing down under the write — must not
    /// share the words an incident is keyed on.
    /// </summary>
    [Fact]
    public void TwoUnrelatedCauses_ProduceTwoDifferentTemplates()
    {
        var missing = Log(NodeTypeReleaseFailure.NodeMissing,
            "Failed to start the release: No node found at 'rbuergi/OperationRequest'. "
            + "Closest ancestor is 'rbuergi' (remainder='OperationRequest').");
        var teardown = Log(NodeTypeReleaseFailure.HostTearingDown,
            "Failed to start the release: Cannot access a disposed object.\n"
            + "Object name: 'MeshNodeStreamCache'.");

        missing.Template.Should().NotBe(teardown.Template,
            "these are the 2026-09-06 and 2026-09-17 sightings that folded onto one incident; a "
            + "release requested for a node that does not exist is not a defect, and a host "
            + "teardown belongs on the teardown family's issue — one template cannot say either");
        missing.Template.Should().Contain("NO NODE EXISTS",
            "the CLASS has to be in the template, not in {Message}: {Message} is a structured "
            + "parameter, and the words outside it are what survive into a ticket's title");
        teardown.Template.Should().Contain("HOST WAS TEARING DOWN");
    }

    /// <summary>
    /// Every declared class carries a template of its own. Two classes sharing one template would be
    /// indistinguishable at exactly the moment someone needs to tell them apart — the defect this
    /// change exists to end, reintroduced one enum member at a time.
    /// </summary>
    [Fact]
    public void EveryFailureClass_GetsATemplateOfItsOwn()
    {
        var classes = Enum.GetValues<NodeTypeReleaseFailure>();
        var templates = classes
            .Select(failure => (Failure: failure, Template: Log(failure, "reason").Template))
            .ToArray();

        templates.Select(t => t.Template).Distinct(StringComparer.Ordinal).Should()
            .HaveCount(classes.Length,
                "one template per class, always. Any duplicate is two causes sharing the words a "
                + "fingerprint keys on, which is #1549 reopened under a new name. Duplicates: "
                + string.Join(", ", templates
                    .GroupBy(t => t.Template, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => string.Join("+", g.Select(t => t.Failure)))));
    }

    /// <summary>
    /// The fallback is NAMED and reached only by <see cref="NodeTypeReleaseFailure.Unclassified"/>.
    /// A fallback that some other class can also reach is a class that silently means "and
    /// everything else" — and then the bucket is back, just wearing a specific name.
    /// </summary>
    [Fact]
    public void TheFallbackTemplate_IsReachedOnlyByUnclassified()
    {
        var fallback = Log(NodeTypeReleaseFailure.Unclassified, "reason").Template;
        fallback.Should().Contain("UNCLASSIFIED",
            "the fallback must say that nothing claimed the shape — an unnamed fallback is "
            + "indistinguishable from a class that happens to be wrong");

        foreach (var failure in Enum.GetValues<NodeTypeReleaseFailure>()
                     .Where(f => f != NodeTypeReleaseFailure.Unclassified))
        {
            Log(failure, "reason").Template.Should().NotBe(fallback,
                $"{failure} is a named cause and must never be reported as unclassified");
        }
    }

    /// <summary>
    /// The class moved into the template; the PATH and the REASON did not. Both stay structured so
    /// everything already built on them — a query filtering on <c>Path</c>, the masking that keeps a
    /// per-path fan-out from becoming a ticket per node — keeps working.
    /// </summary>
    [Fact]
    public void EveryTemplate_KeepsPathAndMessageStructured()
    {
        foreach (var failure in Enum.GetValues<NodeTypeReleaseFailure>())
        {
            var captured = Log(failure, "the reason sentence");
            captured.Template.Should().Contain("{Path}").And.Contain("{Message}",
                $"{failure}'s template must keep both as structured parameters");
            captured.Path.Should().Be(Path);
            captured.Message.Should().Be("the reason sentence");
            // #5629 — HostLeaving is the one class whose level was decided WITH it: a pod that is
            // leaving is not a defect. Every other class keeps Error until its own argument is made.
            captured.Level.Should().Be(
                failure == NodeTypeReleaseFailure.HostLeaving ? LogLevel.Warning : LogLevel.Error,
                "no level moves as a side effect — whether a class deserves a lower one is a "
                + "separate question, per class, with its own cost argument");
        }
    }

    /// <summary>
    /// The four production shapes, classified from the exception each arm actually holds — never
    /// from its text. Each is quoted from <c>Admin/_LogIncident/19be79b3588b8152</c>'s own samples.
    /// </summary>
    [Fact]
    public void TheFoldedProductionShapes_ClassifyToFourDifferentClasses()
    {
        var baseState = new TimeoutException(
            "Update aborted: no initial state arrived for 'Hosting/InstanceAction' within 30s.",
            new MeshNodeStreamHandle.BaseStateTimeoutException(0, -1));
        var ownerUnreachable = new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.OwnerUnreachable, Path,
            "The owner of 'Store/Tier' returned no verdict for this update within 31s."));
        var teardown = new ObjectDisposedException("MeshNodeStreamCache");
        var nodeMissing = new DeliveryFailureException(new DeliveryFailure(
            null!,
            "No node found at 'rbuergi/OperationRequest'. Closest ancestor is 'rbuergi' "
            + "(remainder='OperationRequest').")
        { ErrorType = ErrorType.NotFound });

        // The production sighting came from a pod whose host was going away, so the probe the
        // classifier is handed in production answers true there — see
        // ADisposedDependency_IsTeardownOnlyWhileTheScopeIsGone for the other side of that.
        var classified = new[] { baseState, ownerUnreachable, teardown, (Exception)nodeMissing }
            .Select(ex => NodeTypeReleaseFailureClassifier
                .ClassifyTriggerWriteFault(ex, scopeDisposed: () => true))
            .ToArray();

        classified.Should().Equal(
            [
                NodeTypeReleaseFailure.BaseStateNeverArrived,
                NodeTypeReleaseFailure.OwnerUnreachable,
                NodeTypeReleaseFailure.HostTearingDown,
                NodeTypeReleaseFailure.NodeMissing
            ],
            "four samples on ONE incident, four different defects with four different owners");

        classified.Distinct().Should().HaveCount(4);
        classified.Should().NotContain(NodeTypeReleaseFailure.Unclassified,
            "every shape production has actually produced is claimed by a rule");
    }

    /// <summary>
    /// 🚨 A case on EACH SIDE of the rule that matters most: the base-state class is decided by the
    /// TYPED terminal the base read raises, not by the sentence that wraps it. Same outer message,
    /// same exception type, no typed inner — a foreign timeout, a different defect, a different
    /// class. A text rule would answer identically to both and would silently mis-file every
    /// routing timeout as #1549's original cause.
    /// </summary>
    [Fact]
    public void ABaseStateTimeoutIsTold_ByItsTypedTerminalAndNotByItsSentence()
    {
        const string sentence =
            "Update aborted: no initial state arrived for 'Hosting/InstanceAction' within 30s.";

        NodeTypeReleaseFailureClassifier.ClassifyTriggerWriteFault(
                new TimeoutException(sentence, new MeshNodeStreamHandle.BaseStateTimeoutException(3, 7)))
            .Should().Be(NodeTypeReleaseFailure.BaseStateNeverArrived);

        NodeTypeReleaseFailureClassifier.ClassifyTriggerWriteFault(
                new TimeoutException(sentence, new TimeoutException("The operation has timed out.")))
            .Should().Be(NodeTypeReleaseFailure.TransientHubFailure,
                "the wrapper site carries the ORIGINAL as inner exactly so this stays answerable "
                + "(#2387): an owner that missed the REQUEST budget is described by the same "
                + "sentence and is not the base read running out");
    }

    /// <summary>
    /// 🚨 The OTHER case on each side, and the one that keeps this taxonomy honest about itself: a
    /// bare <see cref="ObjectDisposedException"/> is a teardown race only while the host's own scope
    /// is provably gone. With a LIVE scope the same exception is a genuine disposal defect and must
    /// NOT be labelled <see cref="NodeTypeReleaseFailure.HostTearingDown"/> — that would fold a real
    /// bug onto the teardown family's incident, which is precisely the mistake this change exists to
    /// end, reintroduced inside the fix for it. It reaches the named fallback instead: loud, and on
    /// nobody else's ticket. Same contract as
    /// <c>AreaErrorClassifier.IsHubDisposalRace(ex, scopeDisposed)</c> and <c>ScopeTeardown</c>.
    /// </summary>
    [Fact]
    public void ADisposedDependency_IsTeardownOnlyWhileTheScopeIsGone()
    {
        var disposed = new ObjectDisposedException("MeshNodeStreamCache");

        NodeTypeReleaseFailureClassifier
            .ClassifyTriggerWriteFault(disposed, scopeDisposed: () => true)
            .Should().Be(NodeTypeReleaseFailure.HostTearingDown,
                "the host is going away under an in-flight write — a lifecycle event");

        NodeTypeReleaseFailureClassifier
            .ClassifyTriggerWriteFault(disposed, scopeDisposed: () => false)
            .Should().Be(NodeTypeReleaseFailure.Unclassified,
                "a disposed dependency while the host is ALIVE is a defect; naming it a teardown "
                + "would hide it under an incident somebody else owns");

        NodeTypeReleaseFailureClassifier.ClassifyTriggerWriteFault(disposed)
            .Should().Be(NodeTypeReleaseFailure.Unclassified,
                "no probe means the question was not answered, and an unanswered question must not "
                + "become a yes — the typed-only answer stands");
    }

    /// <summary>
    /// 🚨 #5629 — the router's own shutdown refusal, raced past the leaving gate. The production
    /// line (memex, 2026-09-24 00:49:10Z) read <c>MeshNode Unknown at 'Manufacturing/WorkOrder':
    /// Host is shutting down, cannot route to Manufacturing/WorkOrder</c> and was filed as a
    /// TRANSIENT routing miss at Error, fifteen times in 3 ms. On a host that is LEAVING it is
    /// <see cref="NodeTypeReleaseFailure.HostLeaving"/>, logged at Warning.
    ///
    /// <para>Negative control, on each side: the SAME exception with the probe answering no — a
    /// host that stays reporting its router's refusal — keeps its old class, and so does an
    /// unrelated timeout on a host that IS leaving. Neither half alone decides the class.</para>
    /// </summary>
    [Fact]
    public void TheRoutersShutdownRefusal_IsHostLeavingOnlyOnAHostThatIsLeaving()
    {
        var refusal = new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.Unknown, "Manufacturing/WorkOrder",
            "Host is shutting down, cannot route to Manufacturing/WorkOrder"));

        NodeTypeReleaseFailureClassifier
            .ClassifyTriggerWriteFault(refusal, scopeDisposed: () => false, hostLeaving: () => true)
            .Should().Be(NodeTypeReleaseFailure.HostLeaving);

        var onAStayingHost = NodeTypeReleaseFailureClassifier
            .ClassifyTriggerWriteFault(refusal, scopeDisposed: () => false, hostLeaving: () => false);
        onAStayingHost.Should().NotBe(NodeTypeReleaseFailure.HostLeaving,
            "the text alone is not the class — a host that stays is not leaving");
        onAStayingHost.Should().Be(NodeTypeReleaseFailureClassifier
                .ClassifyTriggerWriteFault(refusal, scopeDisposed: () => false),
            "with the probe answering no, the classifier answers exactly as it did before #5629");

        NodeTypeReleaseFailureClassifier
            .ClassifyTriggerWriteFault(new TimeoutException("The operation has timed out."),
                scopeDisposed: () => false, hostLeaving: () => true)
            .Should().Be(NodeTypeReleaseFailure.TransientHubFailure,
                "the probe alone is not the class either — an unrelated fault during a drain keeps "
                + "its own name");

        var logged = Log(NodeTypeReleaseFailure.HostLeaving, "reason");
        logged.Level.Should().Be(LogLevel.Warning,
            "a leaving pod is not a defect; fifteen fail-level lines per drained pod were the bug");
        logged.Template.Should().Contain("THIS HOST IS LEAVING");
    }

    /// <summary>
    /// A shape nothing recognises reaches the fallback rather than the nearest-looking class — and
    /// the fallback's own template says so. This is the direction that matters: absorbing an unknown
    /// shape into a named class is how a ticket comes to carry a cause it does not have.
    /// </summary>
    [Fact]
    public void AnUnrecognisedShape_ReachesTheNamedFallback()
    {
        NodeTypeReleaseFailureClassifier
            .ClassifyTriggerWriteFault(new InvalidOperationException("no rule claims this"))
            .Should().Be(NodeTypeReleaseFailure.Unclassified);
        NodeTypeReleaseFailureClassifier.ClassifyTriggerWriteFault(null)
            .Should().Be(NodeTypeReleaseFailure.Unclassified);
    }

    private static Captured Log(NodeTypeReleaseFailure failure, string reason)
    {
        var sink = new ConcurrentQueue<Captured>();
        NodeTypeRecompileExtensions.LogReleaseRefusal(
            new CapturingLogger(sink), new NodeTypeReleaseRefusal(Path, failure, reason));
        return sink.Should().ContainSingle(
            $"{failure} must log exactly one line — a class that logs none is invisible and one "
            + "that logs two doubles every count built on it").Which;
    }

    private sealed record Captured(LogLevel Level, string Template, string? Path, string? Message);

    /// <summary>
    /// Captures the TEMPLATE, from <c>{OriginalFormat}</c> — not the formatted string. The formatted
    /// string is what a human reads; the template is the log site's identity, and asserting on the
    /// formatted text would pass just as happily over one template with the class interpolated into
    /// its parameters, which is the state this change ends.
    /// </summary>
    private sealed class CapturingLogger(ConcurrentQueue<Captured> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                return;
            sink.Enqueue(new Captured(
                logLevel,
                Value(values, "{OriginalFormat}") ?? string.Empty,
                Value(values, "Path"),
                Value(values, "Message")));
        }

        private static string? Value(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
        {
            foreach (var pair in values)
                if (string.Equals(pair.Key, key, StringComparison.Ordinal))
                    return pair.Value?.ToString();
            return null;
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
