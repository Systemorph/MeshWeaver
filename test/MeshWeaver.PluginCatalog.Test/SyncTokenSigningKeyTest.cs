using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the signing key's lifecycle arithmetic — the rotation due date and the grace window that
/// keeps tokens in flight working. Pure, with explicit instants: a rotation window you can only
/// exercise by waiting is one nobody pins.
/// </summary>
public class SyncTokenSigningKeyTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshKeyIsNotDueForRotation()
    {
        var key = new SyncTokenSigningKey
        {
            ProtectedCurrent = "k",
            CurrentIssuedAt = Now,
            RotateAfter = Now.Add(SyncTokenSigningKeys.RotationInterval),
        };
        Assert.False(key.RotationDue(Now));
        Assert.False(key.RotationDue(Now.Add(SyncTokenSigningKeys.RotationInterval) - TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void RotationBecomesDueAtTheDueInstant()
    {
        var key = new SyncTokenSigningKey { ProtectedCurrent = "k", RotateAfter = Now };
        // Inclusive here, unlike a licence term: "due at T" should fire AT T, not a tick later.
        Assert.True(key.RotationDue(Now));
        Assert.True(key.RotationDue(Now.AddDays(1)));
    }

    [Fact]
    public void NoDueDateMeansNeverDue()
    {
        // A key with no schedule must not read as perpetually overdue — that would rotate on every
        // read and invalidate tokens continuously.
        var key = new SyncTokenSigningKey { ProtectedCurrent = "k", RotateAfter = null };
        Assert.False(key.RotationDue(Now));
        Assert.False(key.RotationDue(Now.AddYears(10)));
    }

    [Fact]
    public void BeforeTheFirstRotationThereIsNoPreviousKey()
    {
        var key = new SyncTokenSigningKey { ProtectedCurrent = "k", CurrentIssuedAt = Now };
        Assert.False(key.PreviousStillValid(Now));
    }

    [Fact]
    public void TheOutgoingKeyStaysValidThroughTheGraceWindow()
    {
        // The property that stops a rotation from invalidating every token minted just before it.
        var key = Rotated();
        Assert.True(key.PreviousStillValid(Now));
        Assert.True(key.PreviousStillValid(Now.Add(SyncTokenSigningKeys.RotationGrace) - TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void TheOutgoingKeyDiesWhenTheWindowCloses()
    {
        var key = Rotated();
        Assert.False(key.PreviousStillValid(Now.Add(SyncTokenSigningKeys.RotationGrace)));
        Assert.False(key.PreviousStillValid(Now.Add(SyncTokenSigningKeys.RotationGrace).AddHours(1)));
    }

    [Fact]
    public void APreviousKeyWithNoDeadlineIsNotAccepted()
    {
        // Fail closed on a malformed record: an outgoing key with no expiry would otherwise be
        // accepted forever, which is the opposite of what rotating is for.
        var key = new SyncTokenSigningKey
        {
            ProtectedCurrent = "new", ProtectedPrevious = "old", PreviousValidUntil = null,
        };
        Assert.False(key.PreviousStillValid(Now));
    }

    [Fact]
    public void TheGraceWindowIsExactlyOneMaximumTokenLifetime()
    {
        // Any longer keeps a retired key alive for tokens that cannot exist: nothing signed before
        // the rotation can still be unexpired past one maximum lifetime.
        Assert.Equal(SyncAccessToken.MaximumLifetime, SyncTokenSigningKeys.RotationGrace);
    }

    [Fact]
    public void TheKeyIsLongerThanTheMinimumTheTokenDemands()
        => Assert.True(SyncTokenSigningKeys.KeyByteLength >= SyncAccessToken.MinimumSigningKeyBytes);

    [Fact]
    public void ThePathIsFixed_WhichIsWhatMakesTheRaceCollide()
    {
        // Uniqueness comes from two replicas colliding on ONE path. An id that varied by host, pod
        // or time would let every replica mint its own key and nothing would ever collide.
        Assert.Equal("Admin/SyncTokenSigningKey/current", SyncTokenSigningKeys.Path);
        Assert.Equal($"{SyncTokenSigningKeys.Namespace}/{SyncTokenSigningKeys.Id}", SyncTokenSigningKeys.Path);
    }

    private static SyncTokenSigningKey Rotated() => new()
    {
        ProtectedCurrent = "new",
        CurrentIssuedAt = Now,
        ProtectedPrevious = "old",
        PreviousValidUntil = Now.Add(SyncTokenSigningKeys.RotationGrace),
        RotateAfter = Now.Add(SyncTokenSigningKeys.RotationInterval),
    };
}
