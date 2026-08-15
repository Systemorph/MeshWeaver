using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the speech + OpenAI-module residual lanes: installing the DLLs via
/// <see cref="MeshBuilder.InstallAssemblies"/> registers the Whisper transcriber (inert when the
/// <c>Speech</c> section is unconfigured — the mic UI hides and the endpoint 503s) and the
/// OpenAI-compatible model-discovery hosted sync (self-gating on its endpoint config).
/// </summary>
public class SpeechModuleTest
{
    private static IServiceCollection Install(params Type[] anchors)
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());
        builder.InstallAssemblies(anchors.Select(t => t.Assembly.Location).Distinct().ToArray());
        return serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));
    }

    [Fact]
    public void SpeechModule_RegistersTheTranscriber_InertWhenUnconfigured()
    {
        var services = Install(typeof(SpeechModuleAttribute));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        var transcriber = provider.GetRequiredService<ISpeechTranscriber>();
        Assert.False(transcriber.IsConfigured);
    }

    [Fact]
    public void OpenAIModule_CarriesTheModelDiscoverySync()
    {
        var services = Install(typeof(MeshWeaver.AI.OpenAI.OpenAICompatibleModelSync));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(MeshWeaver.AI.OpenAI.OpenAICompatibleModelSync));
    }
}
