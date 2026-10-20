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
