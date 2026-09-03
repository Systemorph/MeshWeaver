using Memex.Portal.Shared.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A <c>Graph:Storage</c> section that exists but names no <c>Type</c> means NO STORAGE — it does
/// not mean "file system".
///
/// <para>🚨 <b>Two changes that are each correct combine into a silent disaster without this.</b>
/// The deployed image states <c>Graph:Storage:UnanchoredQueryPolicy</c> deliberately, so that no
/// chart, ConfigMap or environment can forget it (the image ci.7658 incident, where every signed-in
/// request answered 503). The same image deliberately states no <c>Graph:Storage:Type</c>, so the
/// first-run setup wizard is reachable. The section therefore EXISTS while answering nothing about
/// storage — and <c>GraphStorageConfig.Type</c> carries the initializer <c>FileSystem</c>, so
/// anything that BINDS that section reads a working-looking file-system store.</para>
///
/// <para>The consequences of getting this wrong are both silent: the wizard becomes unreachable
/// again, and a real instance is pointed at container-ephemeral disk — issue #435's shape, arriving
/// from a file nobody edited. So both readers test the RAW key, and this pins that they do.</para>
/// </summary>
public class StorageSectionWithoutTypeIsNotConfiguredTest
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    [Fact]
    public void APolicyOnlySection_ReadsAsAwaitingSetup()
    {
        // Exactly the deployed image's shape after the two changes met.
        var configuration = Config(("Graph:Storage:UnanchoredQueryPolicy", "ServeAndReport"));

        Assert.True(SetupOnlyHost.IsAwaitingSetup(configuration),
            "a section carrying only a query policy states no storage — binding it would yield "
            + "Type=FileSystem from the record's initializer and boot a real instance onto "
            + "container-ephemeral disk.");
    }

    [Fact]
    public void ABlankType_ReadsAsAwaitingSetup()
        // An environment variable cannot be null, only empty — "" is the deployed shape of unset,
        // and it is exactly what the chart emits for a values file that states nothing.
        => Assert.True(SetupOnlyHost.IsAwaitingSetup(
            Config(("Graph:Storage:Type", ""), ("Graph:Storage:UnanchoredQueryPolicy", "ServeAndReport"))));

    [Fact]
    public void NoSectionAtAll_ReadsAsAwaitingSetup()
        => Assert.True(SetupOnlyHost.IsAwaitingSetup(Config()));

    [Fact]
    public void ARealBackend_ReadsAsConfigured()
    {
        // The negative control: without it every assertion above would pass on a predicate that
        // simply always answers true.
        Assert.False(SetupOnlyHost.IsAwaitingSetup(
            Config(("Graph:Storage:Type", "PostgreSql"),
                   ("Graph:Storage:UnanchoredQueryPolicy", "ServeAndReport"))));
        Assert.False(SetupOnlyHost.IsAwaitingSetup(Config(("Graph:Storage:Type", "Sqlite"))));
    }
}
