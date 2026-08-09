using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// The outcome of ONE bounded node read — the seam that keeps "we could not find out" apart from
/// "we found out, and the node is not there" (issue #974, split out of #637).
///
/// <para>Exactly three shapes:
/// <list type="bullet">
///   <item><description><b>Found</b> — the read completed and <see cref="Node"/> is the node.</description></item>
///   <item><description><b>Absent</b> (<see cref="Node"/> <c>null</c>, <see cref="UnavailableReason"/>
///     <c>null</c>) — the read completed and the answer is "there is nothing readable here". A
///     DEFINITIVE negative, and the ONLY case that may be reported to a caller as
///     <c>"Not found: …"</c>.</description></item>
///   <item><description><b>Unavailable</b> (<see cref="UnavailableReason"/> non-null) — the read
///     did NOT complete within its budget, or faulted for an infrastructure reason. NO answer was
///     reached, so the caller must say so.</description></item>
/// </list></para>
///
/// <para><b>Why "Not found" was the expensive lie.</b> A read timeout used to collapse into the
/// same <c>null</c> a genuinely-missing node produces, and every caller rendered that as
/// <c>"Not found: {path}"</c>. That sentence is actionable-looking, and agents act on it: they
/// re-create the node (duplicating it), or they delete and rebuild the "broken" path — against a
/// node that exists and is merely unreachable for a moment. Naming the condition correctly is the
/// entire fix; making the stall rarer is not a fix at all.</para>
///
/// <para><b>A read DENIED by the per-user gate stays Absent, deliberately.</b> Reporting "you may
/// not read this" would disclose that a gated node exists at that exact path — the
/// non-disclosure property the <c>UnauthorizedAccessException</c> leg has always had. Denied is a
/// completed read with a definitive answer ("nothing readable here for you"), so it is not an
/// availability failure and must not become one.</para>
/// </summary>
public sealed record NodeReadOutcome
{
    /// <summary>The node, when the read found one. <c>null</c> on Absent AND on Unavailable —
    /// where it carries no meaning at all.</summary>
    public MeshNode? Node { get; init; }

    /// <summary>Why no verdict was reached, or <c>null</c> when the read produced one
    /// (found OR definitively absent).</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>True when the read reached NO verdict — retryable, never "not found".</summary>
    public bool IsUnavailable => UnavailableReason is not null;

    /// <summary>The read completed and found the node.</summary>
    public static NodeReadOutcome Found(MeshNode node) => new() { Node = node };

    /// <summary>The read completed; there is nothing readable at this path. Definitive.</summary>
    public static NodeReadOutcome Absent { get; } = new();

    /// <summary>The read reached no verdict — report unavailable, never absent.</summary>
    public static NodeReadOutcome Unavailable(string reason) => new() { UnavailableReason = reason };

    /// <summary>
    /// Classifies a read FAULT into Absent vs. Unavailable — decided here, on the TYPED failure,
    /// never by pattern-matching an exception message (which drifts the moment someone rewords a
    /// banner).
    ///
    /// <para>Only two things count as a definitive answer: a per-user read DENIAL (a completed
    /// evaluation — see the type remarks on non-disclosure) and a routing
    /// <see cref="ErrorType.NotFound"/>, which BOTH the monolith router
    /// (<c>RoutingServiceBase.PostNotFound</c>) and the Orleans router (<c>RoutingGrain</c>) stamp
    /// on a genuinely-missing node. Everything else — hub mid-disposal, an Orleans reactivation
    /// reject, a Postgres blackout, a request timeout — is an availability failure.</para>
    ///
    /// <para>🚨 The default is UNAVAILABLE, not Absent. An unrecognised fault is by definition one
    /// we have not reasoned about, and the safe direction is to admit we do not know: a false
    /// "unavailable" costs a retry, while a false "not found" invites deleting a node that
    /// exists.</para>
    /// </summary>
    public static NodeReadOutcome FromReadFailure(string path, Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException)
                return Absent;
            if (e is DeliveryFailureException { Failure.ErrorType: ErrorType.NotFound })
                return Absent;
        }

        return Unavailable(
            $"read of '{path}' faulted: {error.GetType().Name}: {error.Message}");
    }
}
