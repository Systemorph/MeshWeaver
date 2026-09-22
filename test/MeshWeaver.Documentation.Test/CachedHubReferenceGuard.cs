using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A hub REFERENCE cached with <c>??=</c> is a reference to ONE activation, kept for the
/// lifetime of whatever holds it — and nothing in the framework can take it back.</b>
///
/// <para><b>The defect this pins.</b> <c>field ??= someHub.SomeSeam()</c> resolves once and never
/// asks again. The hub behind it is an ordinary hosted hub that can die — a routed
/// <c>DisposeRequest</c>, a recycle, a teardown that wedges — and a hub past <c>Started</c> can
/// serve nothing: its intake refuses every delivery and a direct <c>Observe(...)</c> faults with
/// <c>ObjectDisposedException</c>. From that moment every operation issued through the cached
/// reference is answered <i>"Hub … is shutting down — cannot register new response subject"</i>,
/// for as long as the holder lives, with nothing short of a process restart to recover it.</para>
///
/// <para>🚨 <b>The registry's retire-and-replace does NOT reach it</b>
/// (Systemorph/MeshWeaver#5136). That takes a hub at <c>RunLevel &gt;= ShutDown</c> out from under
/// its ADDRESS so the next LOOKUP mints a successor. It is a fix to the registry; it cannot reach a
/// reference somebody has already put in a field, which is precisely what this guard is about.</para>
///
/// <para><b>What is allowed instead.</b> Cache the ADDRESS — that IS stable, which is why
/// <c>MeshService.NodeOperationTarget</c> may be an outright <c>??=</c> — or revalidate the
/// reference at the read: return the cached hub while it is not winding down, and resolve again
/// when it is. Both sites the sweep found (<c>MeshService.IssuingHub</c>,
/// <c>MeshOperations.ReadHub</c>) now do the second.</para>
///
/// <para>🚨 <b>The rule is about a SEAM's answer, not about every hub-typed field, and the
/// difference is not cosmetic.</b> A first draft of this guard keyed on the field TYPE alone and
/// flagged <c>MessageHubConfiguration.ParentHub</c>
/// (<c>_parentHub ??= ParentServiceProvider?.GetService&lt;IMessageHub&gt;()</c>). That is a
/// DIFFERENT case and revalidating it would be a regression: a hub's parent is never replaced
/// underneath it — the parent's lifetime strictly CONTAINS the child's, since the child is a hosted
/// hub the parent tears down in its own <c>DisposeHostedHubs</c> phase — so there is no successor to
/// pick up, and re-resolving once the parent starts winding down would call
/// <c>GetService</c> on a scope that may already be disposed, which that property's own remarks
/// document as a fault. So the predicate keys on the RIGHT-HAND SIDE: a call to a method whose NAME
/// ends in <c>Hub</c> — the off-router issuing seams, every one of which resolves-or-creates through
/// <c>GetHostedHub</c> and can therefore hand back a SUCCESSOR. A plain
/// <c>GetService&lt;IMessageHub&gt;()</c> is not one of those and is not the subject.</para>
///
/// <para>🚨 <b>The detector is asserted in BOTH directions before it is used</b>
/// (<see cref="TheDetectorFindsTheShapeItIsLookingFor"/> /
/// <see cref="TheDetectorDoesNotFireOnTheShapesThatAreCorrect"/>). A guard whose predicate cannot
/// match is green over an unenforced rule, and on this codebase that has happened: a record-equality
/// guard asked whether a type <i>declares</i> <c>Equals(T)</c>, which a record ALWAYS does, so it
/// passed on the unfixed build having checked nothing. So this one proves it can fire on the exact
/// pre-fix text, and proves it stays silent on the address cache and on the revalidating form, before
/// its zero means anything.</para>
/// </summary>
public class CachedHubReferenceGuard
{
    /// <summary>
    /// Production only — a test may legitimately pin one activation in order to measure it.
    ///
    /// <para>A LOCAL rather than a <c>static readonly string[]</c>: an array is mutable whatever the
    /// field is, so a shared one is process-wide state another test could write through. The scan
    /// configuration is one line and has no reason to outlive the call.</para>
    /// </summary>
    private static string[] ScannedRoots() => ["src"];

    /// <summary>
    /// Field declarations whose type is a hub. Nullable only: a non-nullable field cannot be the
    /// left side of <c>??=</c> in the first place, so widening this would add names that can never
    /// match and make the inventory read larger than the rule.
    /// </summary>
    private static readonly Regex HubField = new(
        @"\b(?:IMessageHub|MessageHub)\s*\?\s+(\w+)\s*(?:;|=)",
        RegexOptions.Compiled);

    [Fact]
    public void NoHubReferenceIsCachedWithoutRevalidation()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = SourceScan.SourceFiles(root, ScannedRoots())
            .SelectMany(f => FindIn(File.ReadAllText(f))
                .Select(field => $"{SourceScan.Relative(root, f)}: {field} ??= …"))
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "🚨 A hub reference is cached with ??= and never revalidated:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nThat pins ONE activation for the holder's lifetime. The hub can die underneath "
            + "it (a DisposeRequest, a recycle, a wedged teardown), and a hub past Started serves "
            + "nothing — every later operation through the cached reference is answered 'Hub … is "
            + "shutting down' until the process restarts. The registry's retire-and-replace acts on "
            + "the ADDRESS and cannot reach a reference in a field (#5136).\n"
            + "Fix: cache the ADDRESS (that is stable), or revalidate at the read — return the "
            + "cached hub while its RunLevel is <= Started and it is not IsDisposing, and resolve "
            + "again otherwise. See MeshService.IssuingHub / UsableIssuingHub.");
    }

    /// <summary>
    /// 🚨 The detector must fire on the exact text this guard exists to forbid — the two pre-fix
    /// lines, verbatim. Without this the zero above is indistinguishable from a pattern that
    /// matches nothing.
    /// </summary>
    [Fact]
    public void TheDetectorFindsTheShapeItIsLookingFor()
    {
        const string preFixMeshService = """
            private IMessageHub? _issuingHub;
            private IMessageHub IssuingHub => _issuingHub ??= hub.NodeOperationIssuingHub();
            """;
        Assert.Equal(["_issuingHub"], FindIn(preFixMeshService));

        const string preFixMeshOperations = """
            private IMessageHub? readHub;

            private IMessageHub ReadHub => readHub ??= hub.ReadIssuingHub();
            """;
        Assert.Equal(["readHub"], FindIn(preFixMeshOperations));

        // The assignment-statement form, which is the same defect written out longhand.
        const string statementForm = """
            private IMessageHub? cached = null;
            private IMessageHub Get()
            {
                cached ??= hub.StreamSubscribingHub();
                return cached;
            }
            """;
        Assert.Equal(["cached"], FindIn(statementForm));
    }

    /// <summary>
    /// 🚨 …and it must stay silent on the two shapes that are CORRECT, or the rule would be
    /// "never cache anything", which is not what is being enforced and would be routed around
    /// rather than obeyed.
    /// </summary>
    [Fact]
    public void TheDetectorDoesNotFireOnTheShapesThatAreCorrect()
    {
        // An ADDRESS is stable — MeshService.NodeOperationTarget, verbatim.
        const string addressCache = """
            private Address? _nodeOperationTarget;
            private Address NodeOperationTarget => _nodeOperationTarget ??= hub.NodeOperationTarget();
            """;
        Assert.Empty(FindIn(addressCache));

        // The revalidating form this guard asks for — MeshService.IssuingHub, verbatim. 🚨 The seam
        // call stays INSIDE the property rather than behind a helper method, because
        // RouterAsNodeOperationOriginRatchetGuard binds a post's receiver to a seam call it can SEE
        // in the same file; this snippet is the shape that satisfies BOTH guards.
        const string revalidating = """
            private IMessageHub? _issuingHub;
            private IMessageHub IssuingHub => _issuingHub = UsableIssuingHub ?? hub.NodeOperationIssuingHub();
            private IMessageHub? UsableIssuingHub =>
                _issuingHub is { IsDisposing: false, RunLevel: <= MessageHubRunLevel.Started } cached
                    ? cached
                    : null;
            """;
        Assert.Empty(FindIn(revalidating));

        // A ??= on something that is not a hub field must not be swept in with it.
        const string unrelated = """
            private ILogger? logger;
            private IMessageHub? liveHub;
            private void Log() => logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("x");
            """;
        Assert.Empty(FindIn(unrelated));

        // 🚨 The PARENT hub — MessageHubConfiguration.ParentHub, verbatim. Not a seam answer: the
        // parent's lifetime CONTAINS this hub's, so there is no successor, and revalidating it once
        // the parent winds down would resolve from a scope that may already be disposed. The first
        // draft of this guard flagged it, which is why it is pinned here rather than exempted in a
        // list somebody would later widen.
        const string parentHub = """
            private IMessageHub? _parentHub;
            public IMessageHub? ParentHub => _parentHub ??= ParentServiceProvider?.GetService<IMessageHub>();
            """;
        Assert.Empty(FindIn(parentHub));
    }

    /// <summary>
    /// Field names of hub-typed fields whose <c>??=</c> right-hand side is a call to an ISSUING
    /// SEAM — a method whose name ends in <c>Hub</c>, which is every off-router seam
    /// (<c>NodeOperationIssuingHub</c>, <c>ReadIssuingHub</c>, <c>MeshReadHub</c>,
    /// <c>NodeOperationExecutionHub</c>, <c>StreamSubscribingHub</c>) and <c>GetHostedHub</c>
    /// itself: all of them resolve-or-create at an address and can hand back a successor. See the
    /// remarks for why a plain <c>GetService&lt;IMessageHub&gt;()</c> is deliberately NOT one.
    ///
    /// <para>Comments and string literals are masked first, so a remark QUOTING the forbidden line
    /// — this guard's own subject matter, and the reason both fixed sites carry one — is not
    /// counted as the line itself.</para>
    /// </summary>
    private static IReadOnlyList<string> FindIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var hubFields = HubField.Matches(code)
            .Select(m => m.Groups[1].Value)
            .Distinct(System.StringComparer.Ordinal)
            .ToArray();
        if (hubFields.Length == 0)
            return [];
        return hubFields
            .Where(name => Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\s*\?\?=[^;]*?\b\w*Hub\s*\("))
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToArray();
    }
}
