using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The instance-level switch that closes a deployment to logged-OUT callers:
/// <c>Access:DenyAnonymous</c> (environment form <c>Access__DenyAnonymous</c>).
///
/// <para><b>Why a switch and not a sweep of grants.</b> Packages install their own
/// <c>{Package}/_Access/Anonymous_Access</c> and <c>Public_Access</c> Viewer grants, and every
/// future install writes more, so an instance that must serve NOTHING anonymously cannot hold that
/// line by deleting grants node by node. With the switch on, every permission check whose subject
/// is <see cref="WellKnownUsers.Anonymous"/> resolves to <see cref="Permission.None"/> no matter
/// which grants, <c>PartitionAccessPolicy.PublicRead</c> policies or <c>NodeTypeGate</c> public
/// surfaces the mesh carries — see <c>Doc/Architecture/AccessControl</c> → "Closing an instance to
/// anonymous callers".</para>
///
/// <para><b>What it does NOT touch.</b> <see cref="WellKnownUsers.Public"/> is the baseline of every
/// SIGNED-IN user, never an unauthenticated caller, so a signed-in user's inherited Public grants
/// keep working. <see cref="WellKnownUsers.System"/>, hub credentials and every other internal
/// identity are unaffected. Nothing that does not ask the mesh for a permission is affected either:
/// the sign-in pages, <c>/health</c>, <c>/ready</c>, <c>/alive</c>, static assets, and the signed
/// webhook inbox (which verifies its HMAC and then writes as System).</para>
///
/// <para><b>Default OFF.</b> Absent, empty or unparseable reads as <c>false</c>, so every
/// deployment that does not state the key behaves exactly as before.</para>
///
/// <para>🚨 Read LIVE on each check (configuration is layered and reloadable), and read ONLY when
/// the subject is anonymous — a signed-in caller's check never pays for it.</para>
/// </summary>
public static class AnonymousAccess
{
    /// <summary>The configuration key. Environment form: <c>Access__DenyAnonymous</c>.</summary>
    public const string DenyAnonymousConfigKey = "Access:DenyAnonymous";

    /// <summary>
    /// True when <paramref name="userId"/> is the logged-out caller — <see cref="WellKnownUsers.Anonymous"/>
    /// (case-insensitively, failing closed) or no identity at all. <see cref="WellKnownUsers.Public"/>
    /// is deliberately NOT anonymous: it is the signed-in baseline.
    /// </summary>
    /// <param name="userId">The subject a check or read is evaluated for.</param>
    /// <returns><c>true</c> for the anonymous subject.</returns>
    public static bool IsAnonymousSubject(string? userId)
        => string.IsNullOrWhiteSpace(userId)
           || string.Equals(userId, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <see cref="DenyAnonymousConfigKey"/> off <paramref name="configuration"/>. Absent,
    /// empty or unparseable means OFF.
    /// </summary>
    /// <param name="configuration">The deployment's configuration, or null.</param>
    /// <returns><c>true</c> only when the key is stated as <c>true</c>.</returns>
    public static bool IsDenied(IConfiguration? configuration)
        => bool.TryParse(configuration?[DenyAnonymousConfigKey], out var denied) && denied;

    /// <summary>
    /// Reads <see cref="DenyAnonymousConfigKey"/> off the <see cref="IConfiguration"/> registered in
    /// <paramref name="services"/>. A provider with no configuration registered is OFF.
    /// </summary>
    /// <param name="services">The service provider of the hub or mesh asking.</param>
    /// <returns><c>true</c> only when the deployment states the switch as <c>true</c>.</returns>
    public static bool IsDenied(IServiceProvider services)
        => IsDenied(services.GetService<IConfiguration>());

    /// <summary>
    /// True when <paramref name="userId"/> is a logged-out CALLER: the anonymous subject, or the id
    /// a VIRTUAL <paramref name="ambient"/> context carries (a logged-out visitor's circuit or
    /// delivery names itself <c>guest-…</c>, not <c>Anonymous</c>, and a caller that passes
    /// <c>captured.ObjectId</c> straight through would otherwise evaluate it as a signed-in user).
    /// </summary>
    /// <param name="userId">The subject being evaluated.</param>
    /// <param name="ambient">The caller's ambient context, when known.</param>
    /// <returns><c>true</c> for a logged-out caller.</returns>
    public static bool IsAnonymousCaller(string? userId, AccessContext? ambient)
        => IsAnonymousSubject(userId)
           || (ambient is { IsVirtual: true }
               && string.Equals(ambient.ObjectId, userId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a check or read for <paramref name="userId"/> must be refused outright because the
    /// caller is logged out (<see cref="IsAnonymousCaller"/>) and this deployment denies anonymous
    /// access. Configuration is consulted only for a logged-out caller.
    /// </summary>
    /// <param name="services">The service provider of the hub or mesh asking.</param>
    /// <param name="userId">The subject being evaluated.</param>
    /// <param name="ambient">The caller's ambient context, when known.</param>
    /// <returns><c>true</c> when the answer must be "no access".</returns>
    public static bool Refuses(IServiceProvider services, string? userId, AccessContext? ambient = null)
        => IsAnonymousCaller(userId, ambient) && IsDenied(services);
}
