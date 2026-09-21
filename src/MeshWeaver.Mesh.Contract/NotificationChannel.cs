using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// The delivery mechanism a <see cref="NotificationChannel"/> uses — the channel-side spelling of
/// <see cref="TransportKind"/>, which is the shared vocabulary and carries the full set.
///
/// <para>🚨 This was an <c>enum</c> and is now <c>const string</c>s with the SAME name and the SAME
/// member spellings, so every <c>NotificationChannelKind.Email</c> call site reads unchanged —
/// policy <c>open-vocabulary-string-constants</c>. The values already persisted as their string
/// name (<c>EnumMemberJsonStringEnumConverter</c> is registered on the hub serializer), so stored
/// content binds across the change untouched.</para>
///
/// <para>Like every vocabulary of this shape it is OPEN: a module may use a transport that appears
/// in neither class. Never validate a channel kind against these members.</para>
/// </summary>
public static class NotificationChannelKind
{
    /// <inheritdoc cref="TransportKind.InApp"/>
    public const string InApp = TransportKind.InApp;
    /// <inheritdoc cref="TransportKind.Email"/>
    public const string Email = TransportKind.Email;
    /// <inheritdoc cref="TransportKind.Teams"/>
    public const string Teams = TransportKind.Teams;
    /// <inheritdoc cref="TransportKind.WhatsApp"/>
    public const string WhatsApp = TransportKind.WhatsApp;
    /// <inheritdoc cref="TransportKind.IMessage"/>
    public const string IMessage = TransportKind.IMessage;
    /// <inheritdoc cref="TransportKind.Sms"/>
    public const string Sms = TransportKind.Sms;
    /// <inheritdoc cref="TransportKind.Webhook"/>
    public const string Webhook = TransportKind.Webhook;
    /// <inheritdoc cref="TransportKind.Log"/>
    public const string Log = TransportKind.Log;
}

/// <summary>
/// A delivery channel a user has configured for notifications (mail, Teams, …). The notification
/// triage agent routes each notification to zero or more of these channels according to the user's
/// <see cref="NotificationRule"/>s. User-authored and owned: stored under
/// <c>{username}/_NotificationChannel/{id}</c>.
/// </summary>
public record NotificationChannel
{
    /// <summary>Unique identifier for the channel.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Human-friendly name shown in settings (e.g. "Work email", "Teams DM").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// How this channel delivers — a <see cref="TransportKind"/> / <see cref="NotificationChannelKind"/>
    /// constant, or a value a module defined itself. Compare against the constants; never treat an
    /// unrecognised value as invalid.
    /// </summary>
    public string Kind { get; init; } = NotificationChannelKind.InApp;

    /// <summary>
    /// Where to deliver — an email address for <see cref="NotificationChannelKind.Email"/>, a Teams
    /// user/conversation id for <see cref="NotificationChannelKind.Teams"/>. When null the channel
    /// targets the owning user's own default address for that kind.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>Whether the channel is active. Disabled channels are skipped by triage.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>User ObjectId that owns the channel.</summary>
    [Browsable(false)]
    public string? CreatedBy { get; init; }
}
