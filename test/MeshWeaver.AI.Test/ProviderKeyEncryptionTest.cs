#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using Memex.Portal.Shared.Models;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// End-to-end encryption-at-rest test (Phase 2). With a master key configured,
/// <see cref="ModelProviderService.CreateProvider"/> must store the ApiKey as
/// AES-GCM ciphertext (never the plaintext), and
/// <see cref="ChatClientCredentialResolver"/> must decrypt it back on Resolve.
/// </summary>
public class ProviderKeyEncryptionTest : AITestBase
{
    public ProviderKeyEncryptionTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Enables ConfigMasterKeyProvider -> encryption on.
                ["Ai:KeyProtection:MasterKey"] = "test-master-key-roundtrip-do-not-use",
            })
            .Build();

        return base.ConfigureMesh(builder)
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "Anthropic",
                ProviderName: "Anthropic",
                Order: 1,
                DisplayLabel: "Anthropic",
                DefaultEndpoint: "https://api.anthropic.com/v1/messages",
                DefaultModelIds: ImmutableArray.Create("claude-opus-4-7"),
                RequiresApiKey: true))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(config);
                services.AddSingleton<ModelProviderService>();
                return services;
            });
    }

    [Fact]
    public async Task ApiKey_IsCiphertextAtRest_AndDecryptedOnResolve()
    {
        var owner = $"user-{Guid.NewGuid():N}";
        const string secret = "sk-ant-PLAINTEXT-SECRET-9999";

        var service = Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();
        var workspace = Mesh.GetWorkspace();

        var result = await service.CreateProvider(owner, "Anthropic", secret)
            .Should().Within(20.Seconds()).Emit();
        var providerPath = result.ProviderNode.Path!;
        var modelId = result.ModelNodes.First().Id;

        // 1. AT REST: the stored ApiKey is enc:-tagged ciphertext, not the plaintext.
        var node = await workspace.GetMeshNodeStream(providerPath)
            .Should().Within(10.Seconds())
            .Match(n => (n.Content as ModelProviderConfiguration)?.ApiKey?.StartsWith("enc:") == true);
        var storedKey = ((ModelProviderConfiguration)node.Content!).ApiKey!;
        storedKey.Should().StartWith("enc:v1:");
        storedKey.Should().NotContain(secret, "the literal key must never be stored in plaintext");

        // 2. ON READ: the resolver decrypts back to the original plaintext.
        //    The provider + model live in the OWNER's partition, so the resolver
        //    must watch it (root-only EnsureSubscription wouldn't see them).
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        resolver.WatchPartition(owner);
        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve(modelId))
            .Should().Within(15.Seconds()).Match(r => r.ApiKey != null);
        resolution.ApiKey.Should().Be(secret, "the resolver decrypts the stored ciphertext for the factory");
    }
}
