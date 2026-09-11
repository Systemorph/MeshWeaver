using MeshWeaver.Mesh.Security;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The registry-side RULES of a two-phase instance-key rotation (<see cref="InstanceKeyRotation"/>,
/// MeshWeaver#2802), pure: which key may stage, which may commit, what stops authenticating when.
///
/// <para>The invariant every case serves: <b>a key the instance may still present is never retired
/// until a key it demonstrably reads is presented back.</b> The first rotation design violated it —
/// the registry retired the old key the moment the operator REPORTED a new hash — and every failure
/// after that point left a pod restart away from a 401 storm.</para>
/// </summary>
public class InstanceKeyRotationRulesTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);

    private static string H(string raw) => InstanceKeys.Hash(raw);

    private static MeshWeaverInstance Instance(string current, string staged = "") => new()
    {
        InstanceId = "memex",
        KeyHash = current,
        PendingKeyHash = staged,
    };

    [Fact]
    public void Stage_KeepsTheCurrentKeyAuthenticating_BesideTheNewOne()
    {
        var old = H("mwi_old");
        var next = H("mwi_next");
        var step = InstanceKeyRotation.Stage(Instance(old), old, next, Now);

        step.Refusal.Should().BeNull();
        step.Changed.Should().BeTrue();
        step.Indexed.Should().Be(next, "the new hash's index entry is written so the new key resolves");
        step.Retired.Should().BeEmpty("🚨 staging retires NOTHING the instance may present — that is the fix");
        InstanceKeyRotation.SlotOf(step.Next, old).Should().Be(InstanceKeySlot.Current);
        InstanceKeyRotation.SlotOf(step.Next, next).Should().Be(InstanceKeySlot.Staged);
    }

    [Fact]
    public void Stage_ReplacesAStagedKeyNobodyHolds_ButIsNeverAuthorisedByAStagedKey()
    {
        var old = H("mwi_old");
        var orphan = H("mwi_orphan");
        var next = H("mwi_next");

        var replaced = InstanceKeyRotation.Stage(Instance(old, orphan), old, next, Now);
        replaced.Refusal.Should().BeNull();
        replaced.Retired.Should().Equal([orphan], "a re-stage by the CURRENT key replaces the earlier staged hash");
        replaced.Next.PendingKeyHash.Should().Be(next);
        replaced.Next.KeyHash.Should().Be(old);

        var byStaged = InstanceKeyRotation.Stage(Instance(old, orphan), orphan, next, Now);
        byStaged.Refusal.Should().Contain("STAGED key",
            "the registry cannot know which key the running pods present, so a staged key never stages another "
            + "— a stage must never be able to retire the current key");
        byStaged.Changed.Should().BeFalse();
        byStaged.Retired.Should().BeEmpty();
    }

    [Fact]
    public void Commit_OnlyByTheStagedKey_AndRefusesTheCurrentKeyWhileOneIsStaged()
    {
        var old = H("mwi_old");
        var next = H("mwi_next");
        var staged = Instance(old, next);

        var byCurrent = InstanceKeyRotation.Commit(staged, old, Now);
        byCurrent.Refusal.Should().Contain("CURRENT key",
            "whoever presents the current key has not received the staged one — retiring theirs would lock them out");
        byCurrent.Retired.Should().BeEmpty();

        var byStaged = InstanceKeyRotation.Commit(staged, next, Now);
        byStaged.Refusal.Should().BeNull();
        byStaged.Retired.Should().Equal([old], "the commit is the ONE place the previous key is retired");
        byStaged.Next.KeyHash.Should().Be(next);
        byStaged.Next.PendingKeyHash.Should().BeEmpty();
        InstanceKeyRotation.SlotOf(byStaged.Next, old).Should().Be(InstanceKeySlot.None);

        var repeat = InstanceKeyRotation.Commit(byStaged.Next, next, Now);
        repeat.Refusal.Should().BeNull("a repeated commit is idempotent");
        repeat.Changed.Should().BeFalse();
    }

    [Fact]
    public void Revoke_StopsExactlyTheKeyItIsAskedTo()
    {
        var old = H("mwi_old");
        var next = H("mwi_next");

        var current = InstanceKeyRotation.RevokePresented(Instance(old, next), old, Now);
        current.Retired.Should().Equal([old]);
        InstanceKeyRotation.SlotOf(current.Next, old).Should().Be(InstanceKeySlot.None);
        InstanceKeyRotation.SlotOf(current.Next, next).Should().Be(InstanceKeySlot.Staged,
            "revoking the current key leaves the staged one — the only other key — authenticating");
        current.Next.KeyRevokedAt.Should().Be(Now);

        var all = InstanceKeyRotation.RevokeAll(Instance(old, next), Now);
        all.Retired.Should().Equal([old, next]);
        InstanceKeyRotation.SlotOf(all.Next, old).Should().Be(InstanceKeySlot.None);
        InstanceKeyRotation.SlotOf(all.Next, next).Should().Be(InstanceKeySlot.None);
        InstanceKeyRotation.RevokeAll(all.Next, Now).Changed.Should().BeFalse("revoking an instance with no key is idempotent");

        InstanceKeyRotation.RevokePresented(Instance(old), H("mwi_stranger"), Now).Refusal
            .Should().NotBeNull("a key that is not the instance's cannot revoke it");
    }

    /// <summary>
    /// An IMMEDIATE adoption supersedes a staged rotation — the staged key is retired even when the
    /// adopted hash is already current (Copilot review on #4055: the old short-cut returned first and
    /// left a staged key nobody installed authenticating), and an already-indexed hash is not
    /// re-indexed.
    /// </summary>
    [Fact]
    public void Adopt_RetiresAStagedKey_EvenWhenTheHashIsAlreadyCurrent()
    {
        var old = H("mwi_old");
        var staged = H("mwi_staged");
        var fresh = H("mwi_fresh");

        var sameHash = InstanceKeyRotation.Adopt(Instance(old, staged), old, Now);
        sameHash.Changed.Should().BeTrue("a staged key is still outstanding, so this is not a no-op");
        sameHash.Retired.Should().Equal([staged]);
        sameHash.Indexed.Should().BeNull("the current hash is already indexed");
        InstanceKeyRotation.SlotOf(sameHash.Next, staged).Should().Be(InstanceKeySlot.None);
        InstanceKeyRotation.SlotOf(sameHash.Next, old).Should().Be(InstanceKeySlot.Current);

        var theStaged = InstanceKeyRotation.Adopt(Instance(old, staged), staged, Now);
        theStaged.Retired.Should().Equal([old]);
        theStaged.Indexed.Should().BeNull("the staged hash already has its index entry");
        theStaged.Next.KeyHash.Should().Be(staged);
        theStaged.Next.PendingKeyHash.Should().BeEmpty();

        var another = InstanceKeyRotation.Adopt(Instance(old, staged), fresh, Now);
        another.Retired.Should().Equal([old, staged]);
        another.Indexed.Should().Be(fresh);

        InstanceKeyRotation.Adopt(Instance(old), old, Now).Changed
            .Should().BeFalse("already current with nothing staged is the one idempotent repeat");
    }

    [Fact]
    public void AnEmptySlot_NeverMatches_AndOnlyAHashShapeIsAccepted()
    {
        InstanceKeyRotation.SlotOf(Instance(""), "").Should().Be(InstanceKeySlot.None);
        InstanceKeyRotation.SlotOf(Instance(H("mwi_a")), "mwi_a").Should().Be(InstanceKeySlot.None,
            "a raw key is never a hash — the registry compares hashes only");
        InstanceKeyRotation.Stage(Instance(H("mwi_a")), H("mwi_a"), "mwi_b", Now).Refusal
            .Should().Contain("SHA-256", "a stage that carries a key instead of its hash is refused before anything is written");
    }
}
