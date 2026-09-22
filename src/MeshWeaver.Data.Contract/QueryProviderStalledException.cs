namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>A query provider never answered, so the fan-in has no snapshot to merge — and says so
/// instead of hanging.</b> Raised by <c>MeshQuery.MergeProviderObservables</c>'s stall terminal
/// (<c>InitialStallProbe</c>) and by nothing else.
///
/// <para>🚨 <b>Why it lives in <c>MeshWeaver.Data.Contract</c></b> — the same reason
/// <see cref="MeshWeaver.Data.StorageFaults"/> does, and stated there: the layers that have to
/// recognise this fault sit on OPPOSITE sides of the assembly graph.
/// <c>MeshWeaver.Hosting</c>'s fan-in raises it, <c>MeshWeaver.Graph</c>'s RLS validator names it in
/// its unavailability report, and <c>MeshWeaver.Layout</c>'s <c>AreaErrorClassifier</c> has to
/// classify it — but <c>MeshWeaver.Mesh.Contract</c> REFERENCES <c>MeshWeaver.Layout</c>, so a type
/// declared there is invisible to the classifier. Matching it by type NAME instead would be a
/// classification by string, which this tree deliberately does not do.</para>
///
/// <para><b>What it means, exactly.</b> The merged <c>Initial</c> frame gates on EVERY registered
/// <c>IMeshQueryProvider</c> emitting one, because the frame is the union of their slices and a
/// missing slice is indistinguishable from an empty one. A provider that COMPLETES without an
/// Initial is counted as empty and NAMED on the frame
/// (<c>QueryResultChange.SilentProviders</c>, MeshWeaver#4557). A provider that neither
/// emits, completes NOR errors has no such representation: it starved the gate for ever. Until
/// this type existed the consumer simply hung — with no error, no log line at the consumer and
/// nothing to grep. Measured on <c>memex</c> over 400 minutes to 2026-09-21T04:12Z: 95
/// <c>No MeshNode emitted for</c> faults (~14/hour) plus 200+ stall-probe warnings that nothing
/// acted on.</para>
///
/// <para>🚨 <b>It is an availability failure, never a verdict.</b> Every consumer that turns a mesh
/// read into a decision has to read it that way: an access-control fold that cannot read its grants
/// answers <c>Undetermined</c> / <c>Unavailable</c> (fail CLOSED, retryable, attributed to the
/// read) — never "denied", which would send a correctly-entitled caller to request permissions they
/// already hold, and never "granted", which would be a hole. See
/// <c>Doc/Architecture/AccessControl</c> → "The fold can produce NO answer, and that is a third
/// outcome".</para>
///
/// <para>🚨 <b>Deliberately NOT a <see cref="TimeoutException"/>.</b> Several catch arms in the tree
/// match that type to mean "MY OWN bound elapsed" and then print their own budget
/// (<c>MeshOperations</c>, <c>MeshNodeCompilationService</c>, <c>BuildProtocolDriver</c>); inheriting
/// it would hand them a fault they would re-attribute to themselves, which is exactly the
/// misattribution <c>StreamPostGuardTimeoutException</c> was minted to prevent, in reverse. The
/// distinct type also keeps it out of <c>TransientStorageFaults.RetryTransientConnect</c>'s class:
/// a stalled provider must be FIXED, never retried behind the caller's back.</para>
///
/// <para><b>The budget is derived, never configured here</b> —
/// <c>MeshWeaver.Mesh.Services.MeshOperationOptions.QueryInitialBudget</c> is rung 4 of the one ladder
/// (<c>Timeout</c> → <c>NestedTimeout</c> → <c>PermissionEstablishmentBudget</c> →
/// <c>QueryInitialBudget</c>), so this terminal is strictly quicker to fire than the permission
/// fold's own bound that encloses it. That ordering is the whole point: this is the only level that
/// can name WHICH provider starved (issue #1198).</para>
/// </summary>
/// <param name="providers">Comma-joined names of the providers that had not emitted an Initial.</param>
/// <param name="budget">The fan-in's own bound, i.e. the budget that elapsed.</param>
/// <param name="query">The query text the fan-in was serving, for attribution.</param>
/// <param name="userId">The identity the query ran under, for attribution.</param>
public sealed class QueryProviderStalledException(
    string providers, TimeSpan budget, string? query, string? userId)
    : Exception(
        $"Query provider(s) [{providers}] did not emit an Initial within the query fan-in's "
        + $"{budget.TotalSeconds:0.###}s bound for query '{query}' (user '{userId}'). The merged "
        + "Initial gates on EVERY provider, so this query has NO snapshot to answer with — it is "
        + "reported as unavailable (retryable) rather than left hanging with no error. This is an "
        + "availability failure, never a permission verdict: a consumer deciding access must fail "
        + "CLOSED and say it could not establish the answer. Fix the stalled provider; never bump "
        + "the consumer's timeout.")
{
    /// <summary>Comma-joined names of the providers that had not emitted an Initial when the
    /// fan-in's bound elapsed — the attribution nothing above this level can produce.</summary>
    public string Providers { get; } = providers;

    /// <summary>The fan-in's own bound (<c>MeshOperationOptions.QueryInitialBudget</c>), never a
    /// consumer's.</summary>
    public TimeSpan Budget { get; } = budget;

    /// <summary>The query text the fan-in was serving.</summary>
    public string? Query { get; } = query;

    /// <summary>The identity the query ran under.</summary>
    public string? UserId { get; } = userId;
}
