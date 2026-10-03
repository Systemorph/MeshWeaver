using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The catalog's <see cref="ITestRunPreflight"/> — the framework's first step of a test run on a
/// mesh that installs packages from a registry (maintainer, 2026-10-04). It reads the TARGET SET
/// from this instance's own registry feed (<see cref="TargetSet"/>, recorded on the reconcile
/// ledger by the last full feed read), reads what is installed (<c>Plugins/*</c>), and — when the
/// run may change the mesh and modules are behind — runs ONE reconcile pass now
/// (<see cref="RegistryUpdateReconciler.ReconcileNow"/>: the same decision boot and the safety net
/// make, held updates and sync-owned convergence included), then re-reads and decides again. A
/// platform behind the target is always a skew: a test run never rolls an image.
/// </summary>
public sealed class CatalogTestRunPreflight(IMessageHub hub, ILogger<CatalogTestRunPreflight>? logger = null)
    : ITestRunPreflight
{
    /// <summary>How long one reading may take — bounded, and an unanswered read is a skew.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    /// <summary>How long the convergence pass may take before the run is refused for it.</summary>
    public static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public IObservable<TestRunPreflightResult> Prepare(bool mayConverge) =>
        Read()
            .SelectMany(first =>
            {
                var verdict = TestRunPreflight.Decide(first.Versions, first.PlatformAtTarget, mayConverge, alreadyConverged: false);
                if (verdict.Kind != TestRunPreflightKind.Converge)
                    return Observable.Return(verdict);
                logger?.LogInformation("[TestRunPreflight] {Message}", verdict.Message);
                var reconciler = hub.ServiceProvider.GetService<RegistryUpdateReconciler>();
                var pass = reconciler is null ? Observable.Return(System.Reactive.Unit.Default) : reconciler.ReconcileNow();
                return pass
                    .Timeout(ConvergeBudget)
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex, "[TestRunPreflight] the convergence pass did not complete");
                        return Observable.Return(System.Reactive.Unit.Default);
                    })
                    .SelectMany(_ => Read())
                    .Select(after => TestRunPreflight.Decide(after.Versions, after.PlatformAtTarget, mayConverge, alreadyConverged: true));
            })
            .Catch((Exception ex) => Observable.Return(new TestRunPreflightResult(
                TestRunPreflightKind.Skew,
                VersionsUnderTest.OfThisProcess() with { Source = "this instance" },
                $"the versions under test could NOT be read ({ex.Message}) — refusing to call a run on an "
                + "unknown version a pass")));

    private IObservable<(VersionsUnderTest Versions, bool PlatformAtTarget)> Read()
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var running = PackagePlatformFloorGate.RunningVersion(hub);

        var served = access.RunAsSystem(() => storage
                .Read(RegistryUpdateReconciler.LedgerPath, hub.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null))
            .Timeout(ReadBudget)
            .Select(node => node?.ContentAs<RegistryReconcileLedger>(hub.JsonSerializerOptions)?.Registries
                .Where(r => r.Served is not null)
                .SelectMany(r => r.Served!)
                .ToImmutableList());

        var records = access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}"))
                .Take(1))
            .Timeout(ReadBudget)
            .Select(change => change.Items
                .Select(n => n.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
                .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => m!)
                .ToImmutableList());

        // The newest ARMED set this instance's self-updater sees (Admin/UpdatePolicy.latestAvailableTag):
        // the target platform, which a sealed-but-unarmed set is not (TargetSet).
        var armed = access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    "path:Admin scope:children nodeType:UpdatePolicy"))
                .Take(1))
            .Timeout(ReadBudget)
            .Select(change => change.Items
                .Select(n => LatestAvailableTag(n.Content))
                .FirstOrDefault(t => t is not null))
            .Catch((Exception _) => Observable.Return<string?>(null));

        return served.Zip(records, armed, (s, r, a) => Compose(running, PlatformBuildInfo.CommitHash, s, r, a));
    }

    private string? LatestAvailableTag(object? content)
    {
        if (content is null)
            return null;
        var element = content is System.Text.Json.JsonElement je
            ? je
            : System.Text.Json.JsonSerializer.SerializeToElement(content, content.GetType(), hub.JsonSerializerOptions);
        return element.ValueKind == System.Text.Json.JsonValueKind.Object
               && element.TryGetProperty("latestAvailableTag", out var tag)
               && tag.ValueKind == System.Text.Json.JsonValueKind.String
            ? tag.GetString()
            : null;
    }

    /// <summary>The reading, pure: installed records against the served feed.</summary>
    /// <param name="running">The running platform version.</param>
    /// <param name="commit">The core commit.</param>
    /// <param name="served">The served feed, or null when no full feed read happened yet.</param>
    /// <param name="records">The install records.</param>
    /// <param name="latestArmed">The newest armed set the instance can see, or null.</param>
    internal static (VersionsUnderTest Versions, bool PlatformAtTarget) Compose(
        string? running, string? commit, ImmutableList<ServedPackage>? served, ImmutableList<PackageManifest> records,
        string? latestArmed = null)
    {
        var byId = served is null ? ImmutableDictionary<string, ServedPackage>.Empty : TargetSet.ById(served);
        var target = served is null ? null : TargetSet.Platform(served, latestArmed);
        var modules = records
            .Select(r =>
            {
                var installed = r.ReleasedVersion ?? r.Version ?? r.ModuleVersion;
                byId.TryGetValue(r.Id, out var s);
                var standing = TargetSet.Standing(r.ModuleVersion, s, target);
                return new ModuleUnderTest(
                    r.Id,
                    installed,
                    standing == TargetStanding.Behind ? s!.Version ?? s.ModuleVersion : null,
                    r.HeldUpdate);
            })
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();
        var versions = new VersionsUnderTest(running, commit, target, modules, "this instance");
        return (versions, target is null || TargetSet.PlatformAtTarget(running, target));
    }
}
