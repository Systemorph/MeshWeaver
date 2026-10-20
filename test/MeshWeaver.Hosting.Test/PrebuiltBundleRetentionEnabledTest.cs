using System.Collections.Generic;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A CONSUMER of the shared artifact store (Doc/Architecture/SharedArtifactStore) reads a bundle store it
/// does not own, on a read-only mount: its retention pass must not be scheduled at all — a report-only
/// pass would still append its ledger under that root, and every consumer would rescan the one store.
/// Pinned: the switch reads from configuration, defaults ON (every existing instance unchanged), and only
/// an explicit <c>false</c> turns it off.
/// </summary>
public class PrebuiltBundleRetentionEnabledTest
{
    private static PrebuiltBundleRetention From(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) dict[k] = v;
        return PrebuiltBundleRetentionExtensions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    [Fact]
    public void ThePassIsOnUnlessSomebodyTurnsItOff()
    {
        Assert.True(PrebuiltBundleRetention.Default.Enabled);
        Assert.True(From().Enabled);
        Assert.True(From((PrebuiltBundleRetentionExtensions.EnabledConfigKey, "not-a-bool")).Enabled);
        Assert.False(From((PrebuiltBundleRetentionExtensions.EnabledConfigKey, "false")).Enabled);
        Assert.Equal("PreWarm:PrebuiltBundleRetention:Enabled", PrebuiltBundleRetentionExtensions.EnabledConfigKey);
    }
}

/// <summary>
/// The STARTUP half of the switch: a disabled pass is never scheduled, even with a bundle root configured
/// and the application started. Observed through <see cref="PrebuiltBundleRetentionStatus"/>: on a host
/// with no mesh the first pass faults at once (the adoption stamps cannot be read), so an ENABLED service
/// records a fault the moment the application starts — the positive control — and a DISABLED one records
/// nothing.
/// </summary>
public class PrebuiltBundleRetentionStartupTest
{
    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public readonly System.Threading.CancellationTokenSource Started = new();
        public System.Threading.CancellationToken ApplicationStarted => Started.Token;
        public System.Threading.CancellationToken ApplicationStopping => System.Threading.CancellationToken.None;
        public System.Threading.CancellationToken ApplicationStopped => System.Threading.CancellationToken.None;
        public void StopApplication() { }
    }

    private static async System.Threading.Tasks.Task<PrebuiltBundleRetentionStatus> StartAndSignal(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedPrebuiltBundles.PublishedRootConfigKey] = System.IO.Path.GetTempPath(),
            [PrebuiltBundleRetentionExtensions.EnabledConfigKey] = enabled ? "true" : "false",
        }).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IConfiguration>(services, configuration);
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var lifetime = new Lifetime();
        var status = new PrebuiltBundleRetentionStatus();
        var service = new PrebuiltBundleRetentionHostedService(provider, lifetime,
            PrebuiltBundleRetentionExtensions.FromConfiguration(configuration), status,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PrebuiltBundleRetentionHostedService>.Instance);
        await service.StartAsync(System.Threading.CancellationToken.None);
        lifetime.Started.Cancel();
        // The pass is kicked from the ApplicationStarted callback; give an async hop a bounded chance.
        var deadline = System.DateTime.UtcNow.AddSeconds(5);
        while (enabled && status.LastFault is null && status.Last is null && System.DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(20);
        service.Dispose();
        return status;
    }

    [Fact]
    public async System.Threading.Tasks.Task AnEnabledPassIsScheduled_ADisabledOneNever()
    {
        var enabled = await StartAndSignal(enabled: true);
        Assert.True(enabled.LastFault is not null || enabled.Last is not null, "positive control: an enabled service ran its first pass once the application started");

        var disabled = await StartAndSignal(enabled: false);
        await System.Threading.Tasks.Task.Delay(200);
        Assert.Null(disabled.LastFault);
        Assert.Null(disabled.Last);
    }
}
