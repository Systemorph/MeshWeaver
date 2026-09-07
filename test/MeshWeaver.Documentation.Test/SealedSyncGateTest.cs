#pragma warning disable CS1591
using System.Collections.Generic;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The decision behind MeshWeaver.Plugins#1430: a module-bearing repository's sync sources advance
/// only to the commit SEALED for this instance's framework identity. Stated as data in both
/// directions — what proceeds, what is held and why, and what is not this gate's business.
///
/// <para>The incident it exists for: 2026-09-06 22:39Z, #1413's Payments split reached both
/// production Stores' <c>Store/*</c> sources (green build → import) while the instances ran a
/// platform whose sealed module set had no Payments module; four Store types compile-errored for
/// nine hours. The green build proves the tree compiles somewhere; the seal proves it compiles here.</para>
/// </summary>
public class SealedSyncGateTest
{
    private static readonly RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");
    private const string Built = "1234567890abcdef1234567890abcdef12345678";
    private const string Sealed = "abcdef1234567890abcdef1234567890abcdef12";
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";

    private static SealedSource Source(string name, string? repository, string? commit, bool sealedState = true, string? refusal = null)
        => new(name, repository, commit, sealedState, refusal);

    [Fact]
    public void NothingSealedForThisIdentity_IsNotThisGatesBusiness()
        => SealedSyncGate.Decide(Plugins, Built, null, [], Identity).Proceed
            .Should().BeTrue("an instance that runs no publication of the repository keeps today's behaviour");

    [Fact]
    public void ASealOfAnotherRepository_DoesNotHoldThisOne()
        => SealedSyncGate.Decide(Plugins, Built, null,
                [Source("education", "Systemorph/MeshWeaver.Education", Sealed)], Identity).Proceed
            .Should().BeTrue("the education seal says nothing about the plugins repository");

    [Fact]
    public void TheSealAtTheBuiltCommit_Proceeds()
        => SealedSyncGate.Decide(Plugins, Built, Sealed,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity).Proceed
            .Should().BeTrue("the seal and the build agree on the tree");

    [Fact]
    public void TheRepositoryMarkerIsMatchedLikeGitHubDoes_CaseInsensitively()
        => SealedSyncGate.Decide(Plugins, Built, null,
                [Source("plugins", "systemorph/meshweaver.plugins", Built)], Identity).Proceed
            .Should().BeTrue();

    [Fact]
    public void ASealAtAnotherCommit_Holds_AndSaysWhere()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, Sealed,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity);
        verdict.Proceed.Should().BeFalse("the instance's module set was sealed at another commit");
        verdict.HoldReason.Should().Contain("built at 12345678")
            .And.Contain("not sealed for this instance")
            .And.Contain("'plugins' is sealed at abcdef12")
            .And.Contain(Identity);
    }

    [Fact]
    public void ATornPublication_Holds_RatherThanGuessing()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built, sealedState: false, refusal: "no completion sentinel")], Identity);
        verdict.Proceed.Should().BeFalse("cannot tell is never clear to proceed");
        verdict.HoldReason.Should().Contain("is not sealed").And.Contain("no completion sentinel");
    }

    [Fact]
    public void ASealWithoutARepositoryMarker_IsAttributedByTheBuiltCommit()
        => SealedSyncGate.Decide(Plugins, Built, null, [Source("plugins", null, Built)], Identity).Proceed
            .Should().BeTrue("a seal at the built commit is this build's seal, marker or not");

    [Fact]
    public void ASealWithoutARepositoryMarker_IsAttributedByTheCommitTheSourceSitsOn_AndHolds()
    {
        // The source sits on the sealed commit (the last import the gate admitted); the new build is
        // not sealed yet — hold, exactly as with the marker.
        var verdict = SealedSyncGate.Decide(Plugins, Built, Sealed, [Source("plugins", null, Sealed)], Identity);
        verdict.Proceed.Should().BeFalse();
        verdict.HoldReason.Should().Contain("sealed at abcdef12");
    }

    [Fact]
    public void ASealWithoutARepositoryMarker_OnAnUnrelatedCommit_IsNoEvidence()
        => SealedSyncGate.Decide(Plugins, Built, Sealed, [Source("plugins", null, "0000000000000000000000000000000000000000")], Identity).Proceed
            .Should().BeTrue("a seal that names neither the build nor the source's commit cannot be attributed — it is not evidence either way");

    [Fact]
    public void ASealAtAnUnknownCommit_ForThisRepository_Holds()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", null)], Identity);
        verdict.Proceed.Should().BeFalse("the lane recorded no commit — the seal cannot be matched to the build");
        verdict.HoldReason.Should().Contain("an unknown commit");
    }

    [Fact]
    public void OneSealAtTheBuiltCommitAmongOthers_Proceeds()
        => SealedSyncGate.Decide(Plugins, Built, Sealed,
                [Source("plugins-old", "Systemorph/MeshWeaver.Plugins", Sealed), Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity)
            .Proceed.Should().BeTrue("any sealed source of the repository at the built commit is enough");

    [Theory]
    [InlineData("Systemorph/MeshWeaver.Plugins", "Systemorph", "MeshWeaver.Plugins")]
    [InlineData(" Systemorph/MeshWeaver.Plugins ", "Systemorph", "MeshWeaver.Plugins")]
    [InlineData("MeshWeaver.Plugins", "", "")]
    [InlineData("a/b/c", "", "")]
    public void TheRepositoryMarkerParsesAsOwnerSlashName_OrMatchesNothing(string marker, string owner, string name)
    {
        var parsed = SealedSyncGate.Parse(marker);
        parsed.Owner.Should().Be(owner);
        parsed.Repo.Should().Be(name);
    }
}
