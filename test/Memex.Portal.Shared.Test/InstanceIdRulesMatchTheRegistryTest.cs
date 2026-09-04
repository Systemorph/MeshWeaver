using System;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Setup;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The setup host's id rule and the REGISTRY's must agree, character for character.
///
/// <para>🚨 They are two copies on purpose — the setup host composes no mesh and cannot reach
/// <c>MeshWeaverInstanceService</c> — and two copies of one rule is exactly the shape that drifts.
/// Drifting APART is not a cosmetic problem here: the id is claimed GLOBALLY and never re-issued, so
/// a rule that is too permissive lets the wizard send an id the registry rejects (after the round
/// trip, on the one surface the instance serves), and one that is too strict refuses ids that would
/// have been fine.</para>
/// </summary>
public class InstanceIdRulesMatchTheRegistryTest
{
    public static TheoryData<string> Candidates() =>
    [
        // The shape the wizard actually mints.
        Guid.NewGuid().ToString("d"),
        "0f8fad5b-d9cb-469f-a165-70867728950e",
        // Boundaries.
        "abc", "ab", "a", "",
        new string('a', 48), new string('a', 49),
        // Hyphen rules.
        "-abc", "abc-", "a--b", "a-b-c",
        // Alphabet.
        "ABC", "roland-macbook", "roland_macbook", "roland macbook", "café-instance",
        // The registry's own examples.
        "memex-cloud", "rbuergi",
    ];

    [Theory]
    [MemberData(nameof(Candidates))]
    public void TheSetupHostAgreesWithTheRegistry(string candidate)
    {
        var registry = MeshWeaverInstanceService.IsValidInstanceId(candidate);
        var setup = InstanceIdRules.IsWellFormed(candidate);

        Assert.True(registry == setup,
            $"'{candidate}': the registry says {registry}, the setup host says {setup}. These are "
            + "two copies of one rule and they have drifted — an id is claimed globally and never "
            + "re-issued, so the disagreement costs a real id.");
    }

    [Fact]
    public void AMintedGuidIsAlwaysAcceptable()
    {
        // The premise of minting guids at all. If this ever fails, every fresh install is refused
        // by the registry on its first action.
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.NewGuid().ToString("d").ToLowerInvariant();
            Assert.True(InstanceIdRules.IsWellFormed(id), $"minted id rejected: {id}");
            Assert.True(MeshWeaverInstanceService.IsValidInstanceId(id), $"registry would reject: {id}");
        }
    }
}
