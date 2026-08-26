namespace MeshWeaver.Mesh;

/// <summary>
/// A user signed in. Posted once per sign-in to each address that asked to hear about it
/// (<c>AddSignInNotificationTarget</c>), fire-and-forget.
///
/// <para><b>This is an EVENT, not a request.</b> Nothing is expected back, no response type exists,
/// and the sender ignores every outcome including a failure to deliver. That is deliberate: sign-in
/// is on the user's critical path and must not acquire a dependency on whether some other partition
/// is present, healthy, or fast. A subscriber that is missing, slow, or broken costs the user
/// nothing.</para>
///
/// <para><b>Why core does not simply call the interested party.</b> The work a subscriber does with
/// this — converging a viewer's app tiles to their packages' current artwork, running standard-pack
/// onboarding — needs knowledge core does not have and should not acquire: what a package's current
/// icon is, what "stale" means for an install, what a viewer's escape hatch from convergence is.
/// Core owns exactly one fact, "a user signed in", and says it out loud. Whoever cares subscribes
/// and keeps their own semantics. Handing core an interface to call instead would put that
/// knowledge back in core wearing a different shape.</para>
///
/// <para>🚨 It carries a user PATH, not a session or a token. A subscriber acts on the user's own
/// nodes under the identity the delivery already carries; there is nothing here to authenticate
/// with and nothing worth replaying.</para>
/// </summary>
/// <param name="UserPath">The signing-in user's node path — the partition their own nodes live in.</param>
public record UserSignedIn(string UserPath);
