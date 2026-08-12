using System.Collections.Generic;
using System.Linq;

namespace MeshWeaver.Messaging;

/// <summary>One still-pending request callback, as the diagnostic sees it.</summary>
/// <param name="RequestType">The request's type name.</param>
/// <param name="Target">The hub the reply is expected from.</param>
/// <param name="DiagnosticKey">
/// The request's own sub-key when it opted in (<see cref="IDiagnosticKeyed"/>), else null.
/// </param>
public sealed record PendingCallbackInfo(string RequestType, string? Target, string? DiagnosticKey);

/// <summary>
/// Renders the per-(type, target) tally that closes the <c>[STALE-CALLBACK]</c> line once there are
/// more pending callbacks than the line lists individually.
///
/// <para>🚨 Extracted so it can be tested directly. It is the half that failed to be useful on
/// memex-cloud 2026-08-12: 167 pending <c>SubscribeRequest</c>s to one activity node collapsed into
/// <c>SubscribeRequest@…×147</c> — a single bucket that cannot distinguish 147 SEPARATE streams (a
/// fan-out) from ONE stream re-asking 147 times (a retry loop), though the two have opposite fixes.
/// <c>keys=</c> is that discriminator, and it only appears for request types that opt into
/// <see cref="IDiagnosticKeyed"/>, so nothing about other types' output changes.</para>
/// </summary>
public static class PendingCallbackReport
{
    /// <summary>
    /// Groups by (request type, target), ordered by descending count, and appends <c>keys=N</c> —
    /// the number of DISTINCT diagnostic keys in the group — whenever the group's requests carry
    /// one. <c>keys</c> ≈ count means a fan-out; <c>keys=1</c> means one thing asked repeatedly.
    /// </summary>
    public static string Tally(IEnumerable<PendingCallbackInfo> pending)
        => string.Join(", ", pending
            .GroupBy(p => $"{p.RequestType}@{p.Target}")
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var keys = g.Where(p => p.DiagnosticKey is { Length: > 0 })
                    .Select(p => p.DiagnosticKey!)
                    .Distinct()
                    .Count();
                return keys == 0 ? $"{g.Key}×{g.Count()}" : $"{g.Key}×{g.Count()} keys={keys}";
            }));
}
