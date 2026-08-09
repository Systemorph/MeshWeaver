using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The outcome of ONE bounded identity-side read — the seam that keeps
/// "we could not find out" apart from "we found out, and the answer is no" (issue #637).
///
/// <para>Exactly two shapes:
/// <list type="bullet">
///   <item><description><b>Resolved</b> (<see cref="UnavailableReason"/> <c>null</c>) — the read
///     completed and <see cref="Value"/> IS the answer. A <c>null</c>/empty
///     <see cref="Value"/> here is a DEFINITIVE negative: this user genuinely has no account /
///     no role grants.</description></item>
///   <item><description><b>Unavailable</b> (<see cref="UnavailableReason"/> non-null) — the read
///     did NOT complete within its budget, or faulted. NO answer was reached, so the caller must
///     report unavailability (503 + <c>Retry-After</c>), NEVER a negative identity verdict.</description></item>
/// </list></para>
///
/// <para>Why the distinction has to live HERE and not further up: only the read knows whether it
/// timed out. Collapsing a timeout into the same <c>null</c> an absent row produces is what made a
/// degraded silo tell a correctly-signed-in user "you have no account — sign up" (and, on the API
/// side, "401 invalid token"). Once collapsed, no caller can tell them apart — a message-string
/// sniff further up the stack is a guess, not a fact.</para>
/// </summary>
/// <typeparam name="T">The value the read produces (a mesh node, a role set, …).</typeparam>
public sealed record IdentityReadOutcome<T> where T : class
{
    /// <summary>The resolved value. <c>null</c> on a DEFINITIVE negative — and always <c>null</c>
    /// when <see cref="IsUnavailable"/>, where it carries no meaning at all.</summary>
    public T? Value { get; init; }

    /// <summary>Why no verdict was reached, or <c>null</c> when the read produced one.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>True when the read reached NO verdict — retryable, never a denial.</summary>
    public bool IsUnavailable => UnavailableReason is not null;

    /// <summary>The read completed; <paramref name="value"/> is the answer (possibly a definitive <c>null</c>).</summary>
    public static IdentityReadOutcome<T> Resolved(T? value) => new() { Value = value };

    /// <summary>The read reached no verdict — the caller must answer retryable, never a denial.</summary>
    public static IdentityReadOutcome<T> Unavailable(string reason) => new() { UnavailableReason = reason };
}

/// <summary>
/// Bounds an identity-side read and CLASSIFIES its outcome (issue #637).
///
/// <para>This is the one place a budget is applied to an identity lookup, so it is also the one
/// place that knows a lookup gave up. Every consumer branches on
/// <see cref="IdentityReadOutcome{T}.IsUnavailable"/> instead of re-deriving the distinction from a
/// null value or an exception message.</para>
/// </summary>
public static class IdentityRead
{
    /// <summary>
    /// Runs <paramref name="source"/> under <paramref name="budget"/> and maps it to an
    /// <see cref="IdentityReadOutcome{T}"/>: a value (or a definitive <c>null</c>) becomes
    /// <c>Resolved</c>; a budget overrun or a fault becomes <c>Unavailable</c>.
    ///
    /// <para>🚨 The fault leg is a CLASSIFICATION, not a swallow: the reason is carried out to the
    /// caller (which turns it into 503 + <c>Retry-After</c>) and logged at Warning with the
    /// operation name. Nothing is defaulted to a negative verdict.</para>
    /// </summary>
    /// <param name="source">The read. Composed lazily — a synchronous throw while building it is
    /// classified too (callers wrap their composition in <see cref="Observable.Defer{TResult}(Func{IObservable{TResult}})"/>).</param>
    /// <param name="budget">Wall-clock budget for reaching a verdict.</param>
    /// <param name="operation">Human-readable operation name for the log line (e.g. <c>FindUserByEmail(a@b.c)</c>).</param>
    /// <param name="logger">Optional logger; the Warning naming the operation is the operator's signal.</param>
    public static IObservable<IdentityReadOutcome<T>> Bounded<T>(
        IObservable<T?> source, TimeSpan budget, string operation, ILogger? logger)
        where T : class
        => source
            .Select(IdentityReadOutcome<T>.Resolved)
            .Timeout(budget, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "{Operation}: no verdict within {Timeout} — identity read UNAVAILABLE (retryable infrastructure fault), NOT a negative verdict about this user",
                    operation, budget);
                return Observable.Return(IdentityReadOutcome<T>.Unavailable(
                    $"{operation} reached no verdict within {budget.TotalSeconds:0.##}s"));
            }))
            .Catch<IdentityReadOutcome<T>, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "{Operation}: faulted — identity read UNAVAILABLE (retryable infrastructure fault), NOT a negative verdict about this user",
                    operation);
                return Observable.Return(IdentityReadOutcome<T>.Unavailable(
                    $"{operation} faulted: {ex.GetType().Name}"));
            });
}
