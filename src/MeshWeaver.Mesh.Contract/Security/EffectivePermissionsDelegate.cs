using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Delegate that produces the effective <see cref="Permission"/> set for
/// <paramref name="userId"/> on <paramref name="nodePath"/>. Injected into a
/// hub's <see cref="MessageHubConfiguration"/> via
/// <see cref="MessageHubPermissionExtensions.WithPermissionEvaluator"/>;
/// <c>HubPermissionExtensions</c> reads it back at every permission check.
/// When no delegate is configured, the default returns
/// <see cref="Permission.All"/> (no gating). <c>AddRowLevelSecurity()</c>
/// configures the real <see cref="PermissionEvaluator.GetEffectivePermissions(MeshWeaver.Messaging.IMessageHub, string, string)"/>
/// on the default node hub.
///
/// <para>Choosing the implementation at <em>configuration</em> time — not at
/// each call site — keeps the calling code symmetric across RLS-on /
/// RLS-off hubs. Application code always calls <c>hub.GetEffectivePermissions</c>;
/// the lambda the hub was configured with decides what actually happens.</para>
/// </summary>
public delegate IObservable<Permission> EffectivePermissionsDelegate(
    IMessageHub hub, string nodePath, string userId);

/// <summary>
/// <see cref="MessageHubConfiguration"/> extension for injecting an
/// <see cref="EffectivePermissionsDelegate"/>. Same shape as
/// <c>config.WithType&lt;T&gt;()</c> / <c>config.WithRequestTimeout(...)</c>
/// — fluent, hub-scoped, read via <c>hub.Configuration.Get&lt;T&gt;()</c>.
/// </summary>
public static class MessageHubPermissionExtensions
{
    /// <summary>
    /// Enables row-level security on this hub. Permission checks
    /// (<c>hub.CheckPermission</c> / <c>hub.GetEffectivePermissions</c>)
    /// evaluate against AccessAssignment nodes via
    /// <see cref="PermissionEvaluator"/>; without this call, every check
    /// returns <see cref="Permission.All"/>.
    /// </summary>
    public static MessageHubConfiguration AddRowLevelSecurity(this MessageHubConfiguration config)
        => config.WithPermissionEvaluator(PermissionEvaluator.GetEffectivePermissions);

    /// <summary>
    /// Lower-level overload: inject a custom <see cref="EffectivePermissionsDelegate"/>
    /// (for tests / non-standard evaluators).
    /// </summary>
    public static MessageHubConfiguration WithPermissionEvaluator(
        this MessageHubConfiguration config,
        EffectivePermissionsDelegate evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        return config.Set(evaluator);
    }

    /// <summary>Default evaluator: no RLS, full access everywhere.</summary>
    public static readonly EffectivePermissionsDelegate DefaultEvaluator =
        (_, _, _) => Observable.Return(Permission.All);
}
