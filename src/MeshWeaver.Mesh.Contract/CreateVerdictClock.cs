using System.Reactive.Concurrency;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>The clock the create-or-update handler's inner-create verdict bound runs on</b>
/// (<c>MeshExtensions.InnerCreateVerdictBound</c>) — an instance-scoped seam, registered in the
/// mesh's DI and resolved from the handling hub's <c>ServiceProvider</c>.
///
/// <para><b>Why the seam exists.</b> "The inner create produced no verdict within its bound" is a
/// BEHAVIOUR with its own refusal sentence (<c>activity.node.upsert.createNoVerdict</c>, which must
/// say the outcome is unknown and never that the create was not applied). Pinning that behaviour on
/// the wall clock meant a test sat out the full production bound — 36 s locally — every run, and a
/// test that passes only by waiting out a real-time window is a defect. A test registers a virtual
/// scheduler here and advances it past the bound; the bound itself is never shortened.</para>
///
/// <para><b>Production default: the real clock.</b> Nothing registers this type in production, and
/// an absent registration resolves to <see cref="DefaultScheduler.Instance"/> — the scheduler the
/// <c>Timeout</c> used before the seam existed. Mesh-scoped by construction (a DI singleton of the
/// mesh that registers it), never static, so a virtual clock in one test mesh cannot reach another.</para>
/// </summary>
/// <param name="scheduler">The scheduler the inner-create verdict bound is timed on.</param>
public sealed class CreateVerdictClock(IScheduler scheduler)
{
    /// <summary>The scheduler the inner-create verdict bound is timed on.</summary>
    public IScheduler Scheduler { get; } = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    /// <summary>
    /// The scheduler a hub's inner-create verdict bound runs on: the registered clock's, or the real
    /// clock when none is registered (production).
    /// </summary>
    /// <param name="services">The handling hub's service provider.</param>
    internal static IScheduler For(IServiceProvider services)
        => (services.GetService(typeof(CreateVerdictClock)) as CreateVerdictClock)?.Scheduler
           ?? DefaultScheduler.Instance;
}
