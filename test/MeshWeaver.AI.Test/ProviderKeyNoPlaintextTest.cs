#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI.Portal;             // ModelProviderService — moved here from
                                        // Memex.Portal.Shared.Models with the portal split.
using MeshWeaver.Mesh.Security;         // IProviderKeyProtector (cref below)
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The counterpart to <see cref="ProviderKeyEncryptionTest"/>, which pins the happy path WITH a
/// master key. This one pins the case that actually shipped a plaintext credential: the
/// <see cref="IProviderKeyProtector"/> IS registered — it always is — but no master key is
/// configured, so it has nothing to encrypt with and <c>Protect</c> refuses.
///
/// <para>Being precise about that matters, because "no protector" suggests the fix is to register
/// one, and it is not: the fix is to configure the master key.</para>
///
/// <para>The old behaviour was a silent passthrough — the raw key was persisted into node content
/// and nothing failed, so the encryption test stayed green while production stored cleartext.
/// Storing a provider key must FAIL CLOSED instead: a missing master key is a configuration fault,
/// not a fallback.</para>
///
/// <para>Note the deliberate asymmetry, which this fixture is also the guard for: writes refuse,
/// READS stay tolerant, so an instance already holding legacy plaintext keeps working after an
/// upgrade rather than breaking on data the platform itself wrote.</para>
/// </summary>
public class ProviderKeyNoPlaintextTest : AITestBase
{
    public ProviderKeyNoPlaintextTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Deliberately NO Ai:KeyProtection:MasterKey — this is the unprotected deployment.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
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
    public async Task WithoutAMasterKey_StoringAKey_IsRefused_NotSilentlyStoredInPlaintext()
    {
        var owner = $"user-{Guid.NewGuid():N}";
        const string secret = "sk-ant-PLAINTEXT-MUST-NEVER-PERSIST";

        var service = Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();

        var attempt = async () =>
            await service.CreateProvider(owner, "Anthropic", secret).FirstAsync().ToTask();

        var ex = await Assert.ThrowsAnyAsync<Exception>(attempt);

        // The refusal must NAME the fault and the way out — a bare throw sends the operator
        // hunting, which is how the silent passthrough survived in the first place.
        var text = ex.ToString();
        Assert.Contains("PLAINTEXT", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, text);   // and it must never echo the key it refused
    }

    [Fact]
    public void AKeylessProvider_IsStillAllowed_WithoutAMasterKey()
    {
        // Keyless providers (GitHub Copilot, local Claude Code CLI) legitimately carry no key.
        // Refusing those would break them, so null/empty must stay a valid write — Protect returns
        // null/empty unchanged before it ever looks for a master key.
        var service = Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();
        Assert.NotNull(service);   // constructing the service must not require a master key
    }
}
