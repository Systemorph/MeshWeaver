using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The DECISION an installed-app record makes about its own icon: adopt the icon of the app it
/// points at, or leave the record alone. Pure and hub-free, so every branch is unit-testable — and
/// every branch is a distinct way to get this wrong (overwrite a good icon, rewrite the same
/// placeholder forever, or adopt from a record that points at itself).
///
/// <para>A record at <c>{owner}/_App/{id}</c> carries its own <see cref="MeshNode.Icon"/> so the
/// home's Apps grid can paint from query rows alone — no per-tile hub, no content read. But a
/// record seeded from <c>Admin/HomeConfig.DefaultApps</c>, or written by an install flow with
/// nothing better to hand, gets the generic placeholder, and a grid of identical placeholders
/// defeats the point of an icon grid: you should recognise an app before you read its label.</para>
///
/// <para>🚨 <b>The repair runs as a LOGON ACTION</b>
/// (<see cref="MeshWeaver.Graph.Logon.AppIconAdoptionLogonAction"/>), and the two rejected homes for
/// it are the reason this type is only the decision. Repairing icons inside the home's reactive
/// selector re-ran per SUBSCRIPTION — every navigation and every reconnect — and ran after the
/// ambient access context was cleared, so its query and writes would have executed with NO viewer
/// identity. Moving it to the record hub's initialization fixed the storm but not the identity:
/// initialization is driven by <c>InitializeHubRequest</c>, which carries no viewer context, so it
/// needed <c>ImpersonateAsSystem</c> to do anything at all — and these are the USER's records, which
/// the platform has no business writing as itself. A logon action has a real user identity by
/// construction and fires once per logon session.</para>
///
/// <para>Long term the STORE stamps the real icon when it writes the record and this becomes a
/// no-op. Until then the platform repairs what it renders, because a placeholder grid is a broken
/// feature regardless of which side was supposed to fill it in.</para>
/// </summary>
public static class AppIconAdoption
{
    /// <summary>The placeholder a record wears when nobody supplied a real icon.</summary>
    internal const string GenericIcon = "/static/NodeTypeIcons/puzzlepiece.svg";

    /// <summary>True when a record has no icon, or still wears the placeholder.</summary>
    internal static bool NeedsIcon(MeshNode? record) =>
        record is not null
        && (string.IsNullOrEmpty(record.Icon)
            || string.Equals(record.Icon, GenericIcon, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The app this record opens — <see cref="App.Plugin"/> from the record's CONTENT first, and
    /// <see cref="MeshNode.MainNode"/> only as a fallback. Either way, a value equal to the record's
    /// own path means there is nothing to adopt from.
    ///
    /// <para>🚨 <b>Content first, and this is not a preference.</b> <c>MainNode</c> alone does not
    /// survive the create pipeline for the very records this repair exists for. A default app record
    /// is built with <c>Id = "Store"</c> and <c>MainNode = "Store"</c> (the app it opens) —
    /// <c>UserActivityLayoutAreas.BuildAppRecord</c> — and <c>HandleCreateNodeRequest</c> step 1b'
    /// re-stamps any non-satellite node whose <c>MainNode == Id</c> to its own full path, to stop a
    /// stale bare-id MainNode routing a thread into a phantom partition (the 42P01 bug). That repair
    /// is correct and stays; its side effect is that every default record arrives here pointing at
    /// ITSELF. Resolving the target from <c>MainNode</c> alone therefore yields null for exactly the
    /// records with the generic icon, and the whole feature is silently inert — a green action that
    /// queries, decides "nothing to adopt", and writes nothing, forever.</para>
    ///
    /// <para><c>App.Plugin</c> is documented as "path of the app's root node … the app's identity"
    /// and is untouched by that repair, so it is the reliable answer. MainNode is kept as the
    /// fallback for a record written by some other flow that sets it and no content.</para>
    /// </summary>
    /// <param name="record">The installed-app record node.</param>
    /// <param name="pluginPath">The record content's <see cref="App.Plugin"/>, when it could be read.</param>
    internal static string? TargetOf(MeshNode? record, string? pluginPath = null)
    {
        if (record is null)
            return null;
        return Usable(pluginPath, record.Path) ?? Usable(record.MainNode, record.Path);

        static string? Usable(string? candidate, string ownPath) =>
            !string.IsNullOrWhiteSpace(candidate)
            && !string.Equals(candidate.Trim(), ownPath, StringComparison.OrdinalIgnoreCase)
                ? candidate.Trim()
                : null;
    }

    /// <summary>The icon a record should end up with, given its current state and the target's
    /// icon: <c>null</c> means "leave it alone".</summary>
    internal static string? IconToAdopt(MeshNode? record, string? targetIcon) =>
        NeedsIcon(record)
        && !string.IsNullOrEmpty(targetIcon)
        && !string.Equals(targetIcon, GenericIcon, StringComparison.OrdinalIgnoreCase)
            ? targetIcon
            : null;
}
