#pragma warning disable CS1591

using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The seed's DECISION table, pure: no hub, no configuration system, no key material. Every branch
/// of <see cref="ProviderCredentialSeed.Decide"/> is a different operator outcome, and three of them
/// are load-bearing — "fill the empty field", "never touch an administered one", and "refuse rather
/// than persist plaintext".
/// </summary>
public class ProviderCredentialSeedDecisionTest
{
    private static ModelProviderConfiguration Node(string? apiKey = null, string? endpoint = null) =>
        new() { Provider = "Probe", ApiKey = apiKey, Endpoint = endpoint };

    [Fact]
    public void NoConfiguredKey_IsNotAnError_JustNothingToDo()
        => ProviderCredentialSeed.Decide(Node(), hasConfiguredKey: false, protectionAvailable: true)
            .Should().Be(ProviderSeedOutcome.NoConfiguredKey);

    [Fact]
    public void AbsentNode_IsReported_NotCreated()
        => ProviderCredentialSeed.Decide(null, hasConfiguredKey: true, protectionAvailable: true)
            .Should().Be(ProviderSeedOutcome.NodeAbsent,
                "the static-repo import owns creation; the seed only ever fills a field");

    [Fact]
    public void EmptyKeyOnTheNode_IsSeeded()
        => ProviderCredentialSeed.Decide(Node(apiKey: ""), hasConfiguredKey: true, protectionAvailable: true)
            .Should().Be(ProviderSeedOutcome.Seeded,
                "this is the convergence the create-if-absent seeder could never do");

    [Fact]
    public void AdministeredKey_IsNeverOverwritten()
        => ProviderCredentialSeed.Decide(Node(apiKey: "enc:v1:whatever"), hasConfiguredKey: true, protectionAvailable: true)
            .Should().Be(ProviderSeedOutcome.AlreadyAdministered);

    [Fact]
    public void WhitespaceKey_CountsAsEmpty()
        => ProviderCredentialSeed.Decide(Node(apiKey: "   "), hasConfiguredKey: true, protectionAvailable: true)
            .Should().Be(ProviderSeedOutcome.Seeded);

    [Fact]
    public void WithoutProtection_TheWriteIsRefused_NotDowngraded()
        => ProviderCredentialSeed.Decide(Node(), hasConfiguredKey: true, protectionAvailable: false)
            .Should().Be(ProviderSeedOutcome.RefusedUnprotected);

    [Fact]
    public void RefusalIsCheckedAfterAdministered_SoAKeyedNodeIsNeverReportedAsARefusal()
        => ProviderCredentialSeed.Decide(Node(apiKey: "enc:v1:whatever"), hasConfiguredKey: true, protectionAvailable: false)
            .Should().Be(ProviderSeedOutcome.AlreadyAdministered,
                "a deployment with no master key still has nothing to refuse when the node is already keyed");

    [Theory]
    [InlineData("enc:v1:abc", true)]
    [InlineData("enc:v2:abc", true)]
    [InlineData("sk-plaintext", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProtected_TagsOnly(string? stored, bool expected)
        => ProviderCredentialSeed.IsProtected(stored).Should().Be(expected,
            "the check is on the VALUE about to be persisted, so it holds for any IMasterKeyProvider");

    [Fact]
    public void Endpoint_IsFilledOnlyWhenAbsent()
    {
        ProviderCredentialSeed.EndpointToSeed(Node(), "https://configured/").Should().Be("https://configured/");
        ProviderCredentialSeed.EndpointToSeed(Node(endpoint: "https://administered/"), "https://configured/")
            .Should().BeNull("an administered endpoint is never overwritten either");
        ProviderCredentialSeed.EndpointToSeed(Node(), null).Should().BeNull();
        ProviderCredentialSeed.EndpointToSeed(null, "https://configured/").Should().BeNull();
    }
}
