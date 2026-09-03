namespace MeshWeaver.ContentCollections;

/// <summary>
/// 🚨 <b>Issue #3101.</b> A content sync that FAULTED, re-thrown with the producer's own
/// measurement folded into its message — how many files are individually over the per-delivery
/// budget, which one is largest, its packaged size, and the budget it exceeds.
///
/// <para><b>Why the fault is preserved rather than flattened into a failed response.</b> The
/// transport's refusal reaches the producer as a <c>DeliveryFailureException</c>, and callers
/// classify on that shape (a hub-disposal fault is transient and must stay distinguishable from an
/// application failure — see <c>ContentImportExtensions.FailureFor</c>). Turning it into a
/// successful-looking answer would be the very folding this issue is about, one layer up. So the
/// fault travels on, with the inner exception intact, and only the MESSAGE grows the facts nobody
/// had.</para>
///
/// <para>🚨 <b>The INNER exception is load-bearing, not decoration.</b>
/// <c>PackageInstaller.IsRootRecycling</c> walks the <c>InnerException</c> chain looking for a
/// <c>DeliveryFailureException</c> with <c>ErrorType.ShuttingDown</c> (or a hub disposal), and a
/// transient it fails to recognise becomes "the package's binaries are lost" — the
/// <c>StaleStampRootBindingTest</c> failure. Wrapping keeps that chain intact, so the classification
/// reads exactly what it read before; a wrapper that swallowed the cause would trade one silent
/// mis-report for another.</para>
///
/// <para>Derives from <see cref="InvalidOperationException"/>, so every existing
/// <c>Catch&lt;…, Exception&gt;</c> arm handles it exactly as before.</para>
/// </summary>
/// <param name="message">The refusal, with the producer's budget measurement appended.</param>
/// <param name="innerException">The transport's own failure, unchanged.</param>
public sealed class ContentDeliveryRefusedException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
