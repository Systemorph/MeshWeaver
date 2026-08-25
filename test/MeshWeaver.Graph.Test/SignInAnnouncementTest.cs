using MeshWeaver.Graph.Logon;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The sign-in seam: core says "a user signed in" and knows nothing else.
///
/// <para>The design question this settles is where knowledge lives. Convergence of a viewer's app
/// tiles onto their packages' current artwork needs to know what a package's icon is, what "stale"
/// means for an install, and what a viewer's escape hatch is — Store knowledge. Core holding an
/// interface to call, or posting to the Store's address and tolerating a NotFound, both put that
/// knowledge back into core wearing a different shape. A subscriber registering ITSELF keeps the
/// direction right: the Store knows about core, core does not know about the Store.</para>
/// </summary>
public class SignInAnnouncementTest
{
    private static readonly Address First = new Address("app", "store");
    private static readonly Address Second = new Address("app", "other");

    [Fact]
    public void Nobody_is_notified_until_somebody_asks()
    {
        // The default is silence. A portal where nothing subscribes posts nothing at sign-in —
        // no address to probe, no NotFound to tolerate, no latency to explain.
        new SignInNotificationTargets().Targets.Should().BeEmpty();
    }

    [Fact]
    public void Registering_twice_does_not_double_post()
    {
        // A plugin's configuration can run more than once. Two posts per sign-in would make a
        // subscriber's own idempotence load-bearing for core's bug.
        var targets = new SignInNotificationTargets().Add(First).Add(First);

        targets.Targets.Should().ContainSingle();
    }

    [Fact]
    public void Subscribers_are_notified_in_registration_order()
    {
        var targets = new SignInNotificationTargets().Add(First).Add(Second);

        targets.Targets.Should().Equal(First, Second);
    }

    [Fact]
    public void The_announcement_carries_the_user_path_and_nothing_else()
    {
        // 🚨 A path, not a session or a token. The subscriber acts under the identity the delivery
        // already carries; there is nothing here to authenticate with and nothing worth replaying.
        var announcement = new UserSignedIn("alice");

        announcement.UserPath.Should().Be("alice");
        typeof(UserSignedIn).GetProperties().Should().ContainSingle(
            "an event that grows fields grows coupling — subscribers should read the mesh, not the message");
    }

    [Fact]
    public void It_announces_on_every_sign_in()
    {
        // Run-once would be a contradiction: subscribers exist to react to EACH sign-in, and a
        // ledger entry would silence every one after the first.
        new AnnounceSignInLogonAction().Mode.Should().Be(LogonActionMode.EveryLogon);
    }

    [Fact]
    public void It_announces_after_cores_own_per_user_work()
    {
        // So a subscriber reacting to the announcement sees a settled set of records rather than
        // one mid-repair. Not correctness — the event carries no state — but it removes an ordering
        // question the subscriber would otherwise have to reason about.
        var announce = new AnnounceSignInLogonAction();

        announce.Order.Should().BeGreaterThan(new AppIconAdoptionLogonAction().Order);
    }
}
