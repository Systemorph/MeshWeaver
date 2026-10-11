#nullable enable
using System.Text.Json.Serialization;

namespace MeshWeaver.Data;

/// <summary>
/// What an <see cref="ActivityActionReference"/> does when a person opens it. An OPEN vocabulary of
/// string constants (policy <c>open-vocabulary-string-constants</c>): a module may persist a word of
/// its own, and a renderer that does not know a word reports
/// <see cref="ActivityActionAvailability.UnsupportedKind"/> rather than guessing.
/// </summary>
public static class ActivityActionKind
{
    /// <summary>Open a page on THIS site. The only kind the platform itself resolves.</summary>
    public const string Navigate = "Navigate";
}

/// <summary>
/// Whether a stored <see cref="ActivityActionReference"/> can be offered right now. An OPEN
/// vocabulary of string constants (policy <c>open-vocabulary-string-constants</c>).
///
/// <para>The first four words are STRUCTURAL and are what <see cref="ActivityActionReference.Availability"/>
/// answers from the reference alone. The rest are DOMAIN states only the surface that opens the
/// target can establish (a link was revoked, the viewer may not read the page); they live here so
/// every renderer and every module says the same word for the same state. A word a reader does not
/// know is shown as unavailable and reported verbatim, never treated as
/// <see cref="Available"/>.</para>
/// </summary>
public static class ActivityActionAvailability
{
    /// <summary>The reference is well-formed and this build can resolve it.</summary>
    public const string Available = "Available";

    /// <summary>The reference names a <see cref="ActivityActionReference.Kind"/> this build does not resolve.</summary>
    public const string UnsupportedKind = "UnsupportedKind";

    /// <summary>The reference was written by a newer contract than this build reads.</summary>
    public const string UnsupportedVersion = "UnsupportedVersion";

    /// <summary>The target is not a local path on this site, so it is never navigated to.</summary>
    public const string UnsafeTarget = "UnsafeTarget";

    /// <summary>The thing the reference opens was withdrawn by its publisher.</summary>
    public const string Revoked = "Revoked";

    /// <summary>The thing the reference opens is past its validity window.</summary>
    public const string Expired = "Expired";

    /// <summary>The viewer may not open the target. Established when the reference is OPENED, never stored.</summary>
    public const string Unauthorized = "Unauthorized";

    /// <summary>The target cannot be served right now (its handler or module is not installed here).</summary>
    public const string Unavailable = "Unavailable";
}

/// <summary>
/// A DURABLE action an activity offers after it has run — "Open QR card", "Open the exported
/// document" — stored as plain data on the activity itself (<see cref="ActivityLog.Actions"/>).
///
/// <para><b>Why this exists.</b> A control a script returns reaches a reader by exactly one route:
/// the kernel publishes it into the execution hub's live area dictionary. That dictionary is empty
/// again once the hub unloads, and the JSON left on <see cref="ActivityLog.ReturnValue"/> is hollow
/// for a container control, so a historical activity cannot reopen what it produced. A reference is
/// the part that must outlive the run: a caption and a place to go, nothing that needs the execution
/// hub, the kernel, or the assembly generation that wrote it.</para>
///
/// <para>🚨 <b>It is an ADDRESS, never a grant.</b> The reference carries no credential, no
/// entitlement and no personal data. Whoever renders it re-checks authorization and availability at
/// the moment it is opened; holding the reference proves nothing.</para>
///
/// <para>🚨 <b>It never stores a UI object graph.</b> Every member is a string or a number. A
/// renderer builds its control from these fields on each render.</para>
///
/// <para>Init-only properties rather than a positional record, deliberately: a new optional field is
/// then additive for every module compiled against an earlier build.</para>
/// </summary>
public sealed record ActivityActionReference
{
    /// <summary>The contract version this build writes and the highest it reads.</summary>
    public const int CurrentContractVersion = 1;

    /// <summary>The longest <see cref="Target"/> accepted.</summary>
    public const int MaxTargetLength = 2048;

    /// <summary>
    /// The contract version the reference was written with. A reader refuses a HIGHER one
    /// (<see cref="ActivityActionAvailability.UnsupportedVersion"/>) instead of reading fields whose
    /// meaning may have changed.
    /// </summary>
    public int ContractVersion { get; init; } = CurrentContractVersion;

    /// <summary>
    /// Identifies this action within its activity. Recording a reference whose id the activity
    /// already carries REPLACES that entry, so a retried publication cannot list the action twice.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>One of <see cref="ActivityActionKind"/>, or a module's own word.</summary>
    public string Kind { get; init; } = ActivityActionKind.Navigate;

    /// <summary>
    /// The caption in English — the stored fallback, shown as-is when <see cref="LabelKey"/> is
    /// absent or the catalog does not carry it.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// The localization catalog key that renders <see cref="Label"/> in the viewer's language, or
    /// null. Resolved at RENDER time, so the same stored reference reads correctly for every viewer.
    /// </summary>
    public string? LabelKey { get; init; }

    /// <summary>
    /// Where the action goes: a path on THIS site, beginning with exactly one <c>/</c>
    /// (<c>/a/6f1c…/card</c>). Never an absolute URL — a reference cannot send a viewer to another
    /// origin. See <see cref="IsLocalTarget"/>.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// The mesh path of the domain node that holds what the activity produced, when there is one.
    /// The reference POINTS at the result; it never duplicates it.
    /// </summary>
    public string? ResultPath { get; init; }

    /// <summary>
    /// The key of the action handler the target belongs to, when it belongs to one. Lets a renderer
    /// say <see cref="ActivityActionAvailability.Unavailable"/> when that handler is not installed
    /// on this portal, instead of offering a link to a page nothing serves.
    /// </summary>
    public string? HandlerKey { get; init; }

    /// <summary>The handler version the target was published for, when <see cref="HandlerKey"/> is set.</summary>
    public int? HandlerVersion { get; init; }

    /// <summary>
    /// The STRUCTURAL verdict on this reference: one of
    /// <see cref="ActivityActionAvailability.Available"/>,
    /// <see cref="ActivityActionAvailability.UnsupportedVersion"/>,
    /// <see cref="ActivityActionAvailability.UnsupportedKind"/> or
    /// <see cref="ActivityActionAvailability.UnsafeTarget"/>. Pure — it reads nothing but the
    /// reference, so it answers identically in the run that wrote it and after any restart.
    ///
    /// <para><see cref="ActivityActionAvailability.Available"/> here means "may be OFFERED". The
    /// surface that opens the target still decides revoked, expired and unauthorized.</para>
    /// </summary>
    /// <returns>The availability word.</returns>
    public string Availability()
    {
        if (ContractVersion < 1 || ContractVersion > CurrentContractVersion)
            return ActivityActionAvailability.UnsupportedVersion;
        if (!string.Equals(Kind, ActivityActionKind.Navigate, StringComparison.Ordinal))
            return ActivityActionAvailability.UnsupportedKind;
        return IsLocalTarget(Target)
            ? ActivityActionAvailability.Available
            : ActivityActionAvailability.UnsafeTarget;
    }

    /// <summary>Whether <see cref="Availability"/> is <see cref="ActivityActionAvailability.Available"/>.</summary>
    [JsonIgnore]
    public bool IsAvailable => Availability() == ActivityActionAvailability.Available;

    /// <summary>
    /// Whether <paramref name="target"/> is a path on this site and nothing else. Pure.
    ///
    /// <para>Accepted: a string that starts with exactly one <c>/</c>. Refused: an absolute or
    /// scheme-relative URL (<c>https://…</c>, <c>//host</c>), a backslash anywhere (browsers read
    /// <c>/\host</c> as <c>//host</c>), whitespace and control characters, anything longer than
    /// <see cref="MaxTargetLength"/>, and any of those hidden behind ONE level of percent-encoding
    /// (<c>/%2Fhost</c>, <c>/%5Chost</c>, <c>%0d%0a</c>) — the form a redirect filter that checks
    /// only the raw string lets through. An encoded space (<c>%20</c>) is the one exception: it is
    /// an ordinary path character.</para>
    /// </summary>
    /// <param name="target">The candidate target.</param>
    /// <returns>True when the target can only ever resolve against the current origin.</returns>
    public static bool IsLocalTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Length > MaxTargetLength)
            return false;
        if (!IsLocalShape(target, allowSpace: false))
            return false;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            return false;
        }
        // An ENCODED space (%20) is an ordinary path character; an encoded line break, tab or
        // backslash is not, and neither is a second leading slash.
        return IsLocalShape(decoded, allowSpace: true);
    }

    private static bool IsLocalShape(string value, bool allowSpace)
    {
        if (value.Length == 0 || value[0] != '/')
            return false;
        if (value.Length > 1 && value[1] == '/')
            return false;
        foreach (var c in value)
        {
            if (c == ' ' && allowSpace)
                continue;
            if (c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        }
        return true;
    }
}
