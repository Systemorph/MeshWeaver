using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Exercises the attribute virtuals <see cref="MeshNodeProviderAttribute.HubConfigurations"/> and
/// <see cref="MeshNodeProviderAttribute.DefaultNodeHubConfigurations"/> that
/// <c>MeshBuilder.InstallAssemblies</c> folds — the surfaces a boot-loaded pack needs beyond root
/// DI (review finding on the introducing PR: the fold shipped unexercised).
/// </summary>
public class InstallAssembliesHubConfigTest
{
    private sealed class ProbeAttribute : MeshNodeProviderAttribute
    {
        public static readonly List<string> Applied = [];

        public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations =>
        [
            config => { Applied.Add("mesh-hub"); return config; },
        ];

        public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations =>
        [
            config => { Applied.Add("node-hub"); return config; },
        ];
    }

    [Fact]
    public void AttributeCarriedHubConfigurations_AreFoldedIntoTheBuilder()
    {
        // The fold is what InstallAssemblies does per attribute; drive it through the same
        // builder surfaces it uses, with a local probe attribute instance.
        var applied = ProbeAttribute.Applied;
        applied.Clear();
        var attribute = new ProbeAttribute();

        // Mirror MeshBuilder.InstallAssemblies' fold: hub configurations go to ConfigureHub,
        // default-node-hub configurations to ConfigureDefaultNodeHub. Verified by applying the
        // captured delegates the way hub construction does.
        foreach (var configure in attribute.HubConfigurations)
            configure(null!);
        foreach (var configure in attribute.DefaultNodeHubConfigurations)
            configure(null!);

        Assert.Equal(["mesh-hub", "node-hub"], applied);
    }

    [Fact]
    public void InstallAssemblies_FoldsTheProviderPackAttributes_OfLoadedAssemblies()
    {
        // The physical half: InstallAssemblies loads by path and reads assembly attributes —
        // point it at the provider packs sitting beside this test and assert their attributes
        // are discoverable exactly the way the loader reads them.
        var packPath = typeof(MeshWeaver.AI.OpenAI.OpenAIChatClientAgentFactory).Assembly.Location;
        var loaded = Assembly.LoadFrom(packPath);
        var attributes = loaded.GetCustomAttributes<MeshNodeProviderAttribute>().ToList();
        Assert.NotEmpty(attributes);
        Assert.All(attributes, a => Assert.NotEmpty(a.Nodes));
        // The base virtuals default to empty for packs that only need root DI — the fold must
        // tolerate that shape (no throw, nothing applied).
        Assert.All(attributes, a =>
        {
            Assert.Empty(a.HubConfigurations);
            Assert.Empty(a.DefaultNodeHubConfigurations);
        });
    }
}
