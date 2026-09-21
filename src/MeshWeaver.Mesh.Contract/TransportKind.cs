namespace MeshWeaver.Mesh;

/// <summary>
/// How a party is reached — the shared transport vocabulary for channels, participants and
/// delivery rules.
///
/// <para>🚨 <b>Constants, not an <c>enum</c>, and the set is OPEN</b> — policy
/// <c>open-vocabulary-string-constants</c>. These values are a STARTING set, never the permitted
/// set: any module, satellite or deployment declares its own constants class and puts its own
/// value in the same field, with no registration and no change to core. So never validate a
/// transport against the members of this class, and never coerce an unrecognised one to a default
/// that means something — an unknown transport stays unknown, named and logged.</para>
///
/// <para>Widening an <c>enum</c> instead would break every exhaustive <c>switch</c> under
/// <c>-warnaserror</c>, including in in-mesh NodeType sources that no <c>dotnet build</c> ever
/// type-checks, and an <c>enum</c> deserialising an unknown value yields its ZERO member — here
/// <c>InApp</c>, a real and plausible value — which is a silent wrong answer with nothing logged.</para>
///
/// <para>Resolution over these values is a durable chain of rules, never a <c>switch</c>. See
/// <c>Doc/Architecture/OpenVocabulariesAsStringConstants</c>.</para>
/// </summary>
public static class TransportKind
{
    /// <summary>The in-app notification bell — the always-on default every user has implicitly.</summary>
    public const string InApp = "InApp";

    /// <summary>Email via Microsoft Graph (an outbound <see cref="Email"/> node is created).</summary>
    public const string Email = "Email";

    /// <summary>Microsoft Teams (chat or channel message).</summary>
    public const string Teams = "Teams";

    /// <summary>WhatsApp, addressed by E.164 phone number.</summary>
    public const string WhatsApp = "WhatsApp";

    /// <summary>iMessage, addressed by handle or phone number.</summary>
    public const string IMessage = "IMessage";

    /// <summary>SMS, addressed by E.164 phone number.</summary>
    public const string Sms = "Sms";

    /// <summary>An outbound HTTP webhook.</summary>
    public const string Webhook = "Webhook";

    /// <summary>A log sink — the console, or a log aggregator. A log line is a delivery like any other.</summary>
    public const string Log = "Log";
}
