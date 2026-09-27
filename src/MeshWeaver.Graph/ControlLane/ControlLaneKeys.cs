using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>
/// Which key signs and verifies the lane, on each side — and the refusals that keep the fleet-wide
/// inbox secret out of it (Doc/Architecture/ControlLane → "The key").
///
/// <para>The lane reuses the per-deployment ANNOUNCEMENT key (Doc/Architecture/SelfUpdateAnnouncementKey)
/// in reverse. One vault object per deployment, mounted on both ends:</para>
/// <list type="bullet">
///   <item>on the CONTROL instance as <c>Hosting:PlatformWebhookSecret:{deployment}</c> — where it
///     already verifies that deployment's announcements, and now also signs requests TO it and
///     verifies its reports;</item>
///   <item>on the TARGET as <see cref="TargetKey"/> (<c>ControlLane:Key</c>) — a key of its own name
///     on purpose: mounting it is the explicit act that ARMS the lane, and the target's announcement
///     key (<c>Hosting:ControlInbox:Secret</c>) may still hold the fleet secret on an instance that
///     has not migrated.</item>
/// </list>
/// <para>The request and the report are told apart INSIDE the signed body (<c>kind</c> / <c>event</c>),
/// so one key serving both directions cannot turn a report into a command. Pure over configuration;
/// nothing here returns, logs or stores a key value.</para>
/// </summary>
public static class ControlLaneKeys
{
    /// <summary>The target's lane key. Unset = the lane is not armed on this instance.</summary>
    public const string TargetKey = "ControlLane:Key";

    /// <summary>This instance's own deployment id — the one every request must name.</summary>
    public const string DeploymentKey = "Hosting:Deployment";

    /// <summary>The control instance's inbox secret; its CHILDREN are the per-deployment keys.</summary>
    public const string ControlKeySection = "Hosting:PlatformWebhookSecret";

    /// <summary>The configuration key holding <paramref name="deployment"/>'s own key on the control instance.</summary>
    public static string ControlKeyOf(string deployment) => $"{ControlKeySection}:{deployment}";

    /// <summary>A deployment id: one path segment of letters, digits, <c>-</c>, <c>_</c>, <c>.</c>.</summary>
    private static readonly Regex DeploymentId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$", RegexOptions.Compiled);

    /// <summary>Whether <paramref name="deployment"/> is a well-formed deployment id. Pure.</summary>
    public static bool IsDeploymentId(string? deployment) =>
        deployment is not null && DeploymentId.IsMatch(deployment);

    /// <summary>
    /// Why the lane is NOT armed on this (target) instance, or null when it is. Names keys, never
    /// values. Refuses: no <see cref="TargetKey"/>; no <see cref="DeploymentKey"/>; and a lane key
    /// that EQUALS any secret this instance's webhook inbox verifies with — on a fleet portal that
    /// is the fleet-wide inbox secret, which every CI lane and every portal holds, and a lane armed
    /// with it would accept commands from all of them. Pure.
    /// </summary>
    public static string? ArmingRefusal(IConfiguration? configuration)
    {
        var key = configuration?[TargetKey];
        if (string.IsNullOrWhiteSpace(key))
            return $"the control lane is not armed on this instance: {TargetKey} is not set (mount this deployment's own key there)";
        var self = configuration![DeploymentKey]?.Trim();
        if (string.IsNullOrWhiteSpace(self))
            return $"the control lane is not armed on this instance: {DeploymentKey} is not set, so no request could be checked against the deployment it names";
        if (!IsDeploymentId(self))
            return $"the control lane is not armed on this instance: {DeploymentKey} is not a deployment id";
        foreach (var target in WebhookInbox.ReadTargets(configuration))
        {
            if (target.SecretConfigKey is not { Length: > 0 } secretKey)
                continue;
            if (string.Equals(configuration[secretKey], key, StringComparison.Ordinal))
                return $"the control lane REFUSES to arm: {TargetKey} equals the secret this instance's webhook inbox "
                       + $"verifies '{target.Path}' with ({secretKey}) — an inbox secret is shared by every sender, "
                       + "and a lane armed with it would take commands from all of them. Mount this deployment's own key";
        }
        if (string.Equals(configuration[ControlKeySection], key, StringComparison.Ordinal))
            return $"the control lane REFUSES to arm: {TargetKey} equals {ControlKeySection}, the fleet-wide inbox secret";
        return null;
    }

    /// <summary>
    /// Why the control instance may NOT send a lane request to the instance a record describes, or
    /// null. The record must CLAIM the key (<c>controlLaneKeySecret</c>) — a key the record does not
    /// claim authorises nothing — and, since the control instance holds one key per deployment in
    /// <see cref="ControlKeyOf"/>, a record that also declares an announcement key must name the SAME
    /// vault object. Names only. Pure.
    /// </summary>
    public static string? BindingRefusal(string recordId, string? controlLaneKeySecret, string? announcementKeySecret)
    {
        var lane = (controlLaneKeySecret ?? "").Trim();
        if (lane.Length == 0)
            return $"the record {recordId} declares no controlLaneKeySecret — the control lane reaches only an instance "
                   + "whose record claims a key of its own for it";
        var announcement = (announcementKeySecret ?? "").Trim();
        if (announcement.Length > 0 && !string.Equals(announcement, lane, StringComparison.Ordinal))
            return $"the record {recordId} declares controlLaneKeySecret '{lane}' and announcementKeySecret '{announcement}' — "
                   + $"the control instance holds ONE key per deployment ({ControlKeyOf(recordId)}), so both must name the same vault object";
        return null;
    }

    /// <summary>
    /// The key the CONTROL instance signs a request to <paramref name="deployment"/> with, or the
    /// refusal naming why there is none. Only the deployment's OWN key
    /// (<see cref="ControlKeyOf"/>) — never the shared <see cref="ControlKeySection"/>, and never a
    /// child that equals it (the fleet secret mounted under a deployment's name). Pure.
    /// </summary>
    public static (string? Key, string? Refusal) ControlKeyFor(IConfiguration? configuration, string deployment)
    {
        if (!IsDeploymentId(deployment))
            return (null, $"'{deployment}' is not a deployment id");
        var own = configuration?[ControlKeyOf(deployment)];
        if (string.IsNullOrWhiteSpace(own))
            return (null, $"this control instance holds no key of '{deployment}''s own ({ControlKeyOf(deployment)} is not "
                          + "set) — the control lane signs only with a deployment's own key, never the fleet-wide inbox secret");
        if (string.Equals(configuration![ControlKeySection], own, StringComparison.Ordinal))
            return (null, $"{ControlKeyOf(deployment)} equals the fleet-wide inbox secret {ControlKeySection} — that is "
                          + "not a key of the deployment's own, and the control lane refuses to sign with it");
        return (own, null);
    }
}
