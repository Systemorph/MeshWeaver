using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <b>the link</b> (#1751): a <c>Release</c> node says where its assemblies live, per framework
/// identity and per architecture, and <see cref="ReleaseArtifactResolver"/> is the one place that
/// decides whether any of them is usable by a given consumer.
///
/// <para><b>Why this is guarded rather than merely documented.</b> Every failure mode here is
/// silent. Widen the rule and a consumer adopts bytes from a lane nobody proved compatible — which
/// does not surface as a compile error but as a <c>TypeLoadException</c> inside a collectible ALC at
/// activation, with no overlay and nothing to grep. Narrow it wrongly and the consumer simply
/// compiles, which looks exactly like normal behaviour — the way an arm64 install adopting nothing
/// from an amd64-published lane went unnoticed (#1728). So the rule is pinned in BOTH directions,
/// and so is the decline REASON, because the reason is the only evidence a human gets.</para>
/// </summary>
public class ReleaseArtifactResolverTest
{
    private const string IdentityA = "s1a2b3c4d5e6f708";
    private const string IdentityB = "s9f8e7d6c5b4a302";

    private static NodeTypeRelease Release(
        DateTimeOffset createdAt, params ReleaseArtifact[] artifacts) =>
        new()
        {
            Path = $"Pkg/Type/Release/{createdAt:yyyyMMddHHmmssfff}",
            NodeTypePath = "Pkg/Type",
            Release = "hash",
            FrameworkVersion = "3.0.0.0",
            CreatedAt = createdAt,
            Artifacts = artifacts.Length == 0 ? null : artifacts,
        };

    [Fact]
    public void MatchingIdentityAndArchitectureResolves()
    {
        var artifact = new ReleaseArtifact(IdentityA, "linux-x64", AssemblyStoreVersion: 42);

        var match = ReleaseArtifactResolver.Resolve(
            [Release(DateTimeOffset.UnixEpoch, artifact)], IdentityA, "linux-x64");

        Assert.True(match.IsResolved);
        Assert.Same(artifact, match.Artifact);
        Assert.Equal(42, match.Artifact!.AssemblyStoreVersion);
        Assert.Null(match.DeclineReason);
    }

    [Fact]
    public void SameArchitectureDifferentIdentityDeclines()
    {
        // The identity is the compatibility proof; the architecture never substitutes for it. A
        // consumer that matched on architecture alone would adopt bytes from any build that happened
        // to run on the same hardware.
        var match = ReleaseArtifactResolver.Resolve(
            [Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityB, "linux-x64"))],
            IdentityA, "linux-x64");

        Assert.False(match.IsResolved);
        Assert.Null(match.Artifact);
    }

    [Fact]
    public void SameIdentityDifferentArchitectureDeclines()
    {
        // The narrowing direction. The identity already folds the architecture in, so this pair
        // should not occur in practice — but if a producer ever records it, resolving it would hand
        // an arm64 consumer x64-lane bytes on the strength of a hash collision in the surface
        // manifest. Declining costs one compile.
        var match = ReleaseArtifactResolver.Resolve(
            [Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityA, "linux-x64"))],
            IdentityA, "linux-arm64");

        Assert.False(match.IsResolved);
    }

    [Fact]
    public void ThereIsNoNearestMatch()
    {
        // Neither a prefix of the identity, nor a case variant, nor an OS-less architecture, nor an
        // "x64 is close enough to x86" rule. Every one of these is a plausible-looking widening and
        // every one of them adopts unproven bytes.
        var releases = new[]
        {
            Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityA, "linux-x64")),
        };

        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityA[..8], "linux-x64").IsResolved);
        Assert.False(ReleaseArtifactResolver
            .Resolve(releases, IdentityA.ToUpperInvariant(), "linux-x64").IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityA, "x64").IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityA, "linux-x86").IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityA, "osx-x64").IsResolved);
    }

    [Fact]
    public void ArchitectureMatchIsCaseInsensitiveButIdentityIsNot()
    {
        // A RID is a spelling convention shared across a shell script, a workflow and C#; an identity
        // is a hash the producer emitted. The first tolerates case, the second must not — see
        // PrebuiltAssemblySeederGateTest, which pins the same asymmetry on the seeder's gate.
        var releases = new[]
        {
            Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityA, "Linux-X64")),
        };

        Assert.True(ReleaseArtifactResolver.Resolve(releases, IdentityA, "linux-x64").IsResolved);
    }

    [Fact]
    public void ProducerDeclaredAnyMatchesEveryArchitecture()
    {
        // The ONE widening, and only a PRODUCER may claim it: it asserts the payload is pure IL with
        // nothing architecture-specific. A consumer never widens its own architecture to "any" —
        // "I do not know" is not "it does not matter".
        var releases = new[]
        {
            Release(DateTimeOffset.UnixEpoch,
                new ReleaseArtifact(IdentityA, ReleaseArchitecture.Any)),
        };

        Assert.True(ReleaseArtifactResolver.Resolve(releases, IdentityA, "linux-arm64").IsResolved);
        Assert.True(ReleaseArtifactResolver.Resolve(releases, IdentityA, "win-x64").IsResolved);
        // …but still only inside the identity it was recorded under.
        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityB, "linux-arm64").IsResolved);
    }

    [Fact]
    public void OneReleaseCanCarryBothLanes()
    {
        // The multi-lane shape the amd64/arm64 split needs: two bakes, each recorded honestly under
        // the identity ITS OWN bytes were built against. This is what makes an arm64 install
        // adoptable — never re-publishing one bake's bytes under a second identity, which the CD lane
        // forbids and which would void the proof entirely.
        var releases = new[]
        {
            Release(DateTimeOffset.UnixEpoch,
                new ReleaseArtifact(IdentityA, "linux-x64", AssemblyStoreVersion: 7),
                new ReleaseArtifact(IdentityB, "linux-arm64", AssemblyStoreVersion: 9)),
        };

        Assert.Equal(7, ReleaseArtifactResolver
            .Resolve(releases, IdentityA, "linux-x64").Artifact!.AssemblyStoreVersion);
        Assert.Equal(9, ReleaseArtifactResolver
            .Resolve(releases, IdentityB, "linux-arm64").Artifact!.AssemblyStoreVersion);
    }

    [Fact]
    public void LaterReleaseWins()
    {
        // Old releases are kept on purpose (a live ALC may still hold the previous DLL), so
        // resolution must order rather than assume the collection arrives newest-first.
        var releases = new[]
        {
            Release(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new ReleaseArtifact(IdentityA, "linux-x64", AssemblyStoreVersion: 1)),
            Release(new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
                new ReleaseArtifact(IdentityA, "linux-x64", AssemblyStoreVersion: 2)),
            Release(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                new ReleaseArtifact(IdentityA, "linux-x64", AssemblyStoreVersion: 3)),
        };

        Assert.Equal(2, ReleaseArtifactResolver
            .Resolve(releases, IdentityA, "linux-x64").Artifact!.AssemblyStoreVersion);
    }

    [Fact]
    public void AnUnlinkedReleaseSetDeclinesDifferentlyFromAWrongLane()
    {
        // 🚨 Two DIFFERENT facts, and the whole point of naming them apart: "these releases predate
        // the link" is a producer to upgrade, "these releases are for another lane" is a bake to add.
        // Collapsing both into "not found" is how #1728 stayed invisible for as long as it did.
        var unlinked = ReleaseArtifactResolver.Resolve(
            [Release(DateTimeOffset.UnixEpoch)], IdentityA, "linux-x64");
        var wrongLane = ReleaseArtifactResolver.Resolve(
            [Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityB, "linux-arm64"))],
            IdentityA, "linux-x64");

        Assert.Contains("no release records an artifact link", unlinked.DeclineReason);
        Assert.DoesNotContain("offer", unlinked.DeclineReason);

        Assert.Contains(IdentityA, wrongLane.DeclineReason);
        Assert.Contains("linux-x64", wrongLane.DeclineReason);
        // …and it names what WAS on offer, so the reader can see it is a lane problem, not an outage.
        Assert.Contains($"{IdentityB}/linux-arm64", wrongLane.DeclineReason);
    }

    [Fact]
    public void TheOfferedSetIsDescribedAsAggregate_NotAsOneRelease()
    {
        // The offered lanes are collected across EVERY candidate release, so a reason phrased as
        // "this release offers …" would point the reader at a single node that may hold only part of
        // the set — and this sentence is the whole diagnostic a bundle miss and both ends' logs
        // carry. It has to describe exactly what was looked at.
        var reason = ReleaseArtifactResolver.Resolve(
            [
                Release(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new ReleaseArtifact(IdentityB, "linux-arm64")),
                Release(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                    new ReleaseArtifact(IdentityB, "win-x64")),
            ],
            IdentityA, "linux-x64").DeclineReason;

        Assert.Contains("2 release(s) examined", reason);
        Assert.Contains($"{IdentityB}/linux-arm64", reason);
        Assert.Contains($"{IdentityB}/win-x64", reason);
        Assert.DoesNotContain("this release offers", reason);
    }

    [Fact]
    public void EmptyInputIsAnswered_NotThrown()
    {
        // Total by construction: the resolver runs on a request path and in a boot pass, and a throw
        // there turns a "compile instead" into a failed install.
        Assert.False(ReleaseArtifactResolver.Resolve(null, IdentityA, "linux-x64").IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve([], IdentityA, "linux-x64").IsResolved);
        Assert.NotNull(ReleaseArtifactResolver.Resolve([], IdentityA, "linux-x64").DeclineReason);
    }

    [Fact]
    public void AConsumerThatStatesNoLaneResolvesNothing()
    {
        var releases = new[]
        {
            Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityA, "linux-x64")),
        };

        // Not "unknown means anything" — an unstated identity or architecture is a caller that cannot
        // be shown compatible, which is the same answer as an incompatible one.
        Assert.False(ReleaseArtifactResolver.Resolve(releases, null, "linux-x64").IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve(releases, "  ", "linux-x64").IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityA, null).IsResolved);
        Assert.False(ReleaseArtifactResolver.Resolve(releases, IdentityA, "  ").IsResolved);
        // …and a consumer may NOT pass "any" to widen itself onto every lane.
        Assert.False(ReleaseArtifactResolver
            .Resolve(
                [Release(DateTimeOffset.UnixEpoch, new ReleaseArtifact(IdentityA, "linux-arm64"))],
                IdentityA, ReleaseArchitecture.Any)
            .IsResolved);
    }

    [Fact]
    public void LiveArchitectureIsAPortableRid()
    {
        // The spelling has to compare across the two ends of a distribution link, so it is derived
        // from ProcessArchitecture + OS platform — never RuntimeInformation.RuntimeIdentifier, whose
        // value carries a distro qualifier on some hosts ("debian.12-x64", "linux-musl-x64") and
        // therefore never matches the other end.
        var live = ReleaseArchitecture.Live;

        Assert.Matches(@"^(linux|win|osx)-(x64|x86|arm64|arm)$", live);
        Assert.True(ReleaseArchitecture.Matches(live, live));
        Assert.False(ReleaseArchitecture.Matches(live, null));
        Assert.False(ReleaseArchitecture.Matches(null, live));
    }
}
