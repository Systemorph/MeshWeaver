using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 Policy <c>packages-auto-update</c> applied to the records that already exist: every install
/// record the installer SEEDED with the retired reminder-only default (<c>Notify</c>, or the legacy
/// <c>autoUpdate: false</c> with no policy) is moved to <see cref="PackageUpdatePolicy.Auto"/>.
///
/// <para><b>What is kept, and reported.</b> A policy a global administrator CHOSE on the catalog
/// card (<see cref="PackageManifest.UpdatePolicySetAt"/> stamped by
/// <c>PackageInstaller.SetUpdatePolicy</c>) is a deliberate per-package opt-out and is never
/// overridden; neither is <see cref="PackageUpdatePolicy.None"/> — a pin is never a default.
/// Every kept opt-out is named in one Warning per pass, so the opt-outs in force are visible rather
/// than silent.</para>
///
/// <para>Runs in the boot repair pass (<see cref="InstalledPackageRepairService"/>), one
/// <c>stream.Update</c> per migrated record as System (the install-records partition is
/// System-owned) — never raw SQL. Idempotent: a migrated record no longer matches, so a second
/// pass writes nothing.</para>
/// </summary>
public static class PackageAutoUpdateMigration
{
    /// <summary>Why a record keeps a non-Auto policy, or null when it should be Auto. Pure.</summary>
    /// <param name="record">The install record.</param>
    public static string? KeptOptOut(PackageManifest record)
    {
        var policy = record.EffectiveUpdatePolicy;
        if (policy == PackageUpdatePolicy.Auto)
            return null;
        if (policy == PackageUpdatePolicy.None)
            return "pinned (None)";
        return record.UpdatePolicySetAt is { } at
            ? $"{policy}, chosen by an administrator at {at:u}"
            : null;
    }

    /// <summary>Whether the migration moves <paramref name="record"/> to Auto. Pure.</summary>
    /// <param name="record">The install record.</param>
    public static bool Migrates(PackageManifest record) =>
        record.EffectiveUpdatePolicy != PackageUpdatePolicy.Auto && KeptOptOut(record) is null;

    /// <summary>The record as the migration leaves it (unchanged when it does not migrate). Pure.</summary>
    /// <param name="record">The install record.</param>
    public static PackageManifest Migrated(PackageManifest record) =>
        Migrates(record)
            ? record with { UpdatePolicy = PackageUpdatePolicy.Auto, AutoUpdate = true }
            : record;

    /// <summary>What one pass did.</summary>
    /// <param name="Migrated">The package ids moved to Auto.</param>
    /// <param name="KeptOptOuts">The package ids that keep a non-Auto policy, with why.</param>
    public sealed record Outcome(ImmutableList<string> Migrated, ImmutableList<string> KeptOptOuts);

    /// <summary>
    /// One pass over every install record: migrates the seeded reminder-only ones and reports the
    /// kept opt-outs. Cold; emits ONE <see cref="Outcome"/>; a record whose write fails is logged
    /// and left for the next boot, never failing the pass.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<Outcome> Run(IMessageHub hub, ILogger? logger = null)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        return ModuleDependencyFloor.ReadInstalled(hub)
            .SelectMany(records =>
            {
                var kept = records
                    .Select(r => (r.Id, Why: KeptOptOut(r)))
                    .Where(x => x.Why is not null)
                    .Select(x => $"{x.Id}: {x.Why}")
                    .ToImmutableList();
                var migrating = records.Where(Migrates).Select(r => r.Id).ToImmutableList();
                if (!kept.IsEmpty)
                    logger?.LogWarning(
                        "[PackageAutoUpdate] {Count} installed package(s) keep a deliberate opt-out from "
                        + "auto-update (policy packages-auto-update): {OptOuts}", kept.Count, string.Join("; ", kept));
                return migrating.ToObservable()
                    .Select(id => access.RunAsSystem(() => hub
                            .GetMeshNodeStream($"{PackageInstaller.InstalledPartition}/{id}")
                            .Update<PackageManifest>(Migrated))
                        .Take(1)
                        .Select(_ => (string?)id)
                        .Catch((Exception ex) =>
                        {
                            logger?.LogWarning(ex,
                                "[PackageAutoUpdate] moving {Id} to Auto failed — it stays on its old policy until the next boot", id);
                            return Observable.Return<string?>(null);
                        }))
                    .Concat()
                    .ToList()
                    .Select(done => new Outcome(done.OfType<string>().ToImmutableList(), kept))
                    .Do(outcome =>
                    {
                        if (!outcome.Migrated.IsEmpty)
                            logger?.LogInformation(
                                "[PackageAutoUpdate] moved {Count} installed package(s) from the retired "
                                + "reminder-only default to Auto (policy packages-auto-update): {Ids}",
                                outcome.Migrated.Count, string.Join(", ", outcome.Migrated));
                    });
            });
    }
}
