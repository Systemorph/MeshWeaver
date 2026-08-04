namespace MeshWeaver.Messaging;

/// <summary>
/// Carries a translation of a declaration's user-visible text, next to the declaration itself.
///
/// <para>This is the localization mechanism for everything that HANGS OFF A DECLARATION — property
/// labels, node-type names, enum members, class descriptions. English stays where it already is
/// (<c>[Description]</c> / <c>[Display]</c> / the member name), and each translation rides
/// alongside it, so the two cannot drift apart the way a key-indirected resource table allows:</para>
///
/// <code>
/// [Description("Display time zone (IANA)")]
/// [Translation("de", "Anzeige-Zeitzone (IANA)")]
/// public string? TimeZoneId { get; init; }
/// </code>
///
/// <para>Text with NO declaration to hang off — Blazor markup, inline
/// <c>Controls.Button("…")</c> literals, toasts — is localized through the string catalog instead
/// (<c>LocalizationCatalog</c> / <c>AccessService.Localize</c>). Both shapes resolve the viewer's
/// language through <see cref="Locales.Resolve"/>, so they cannot disagree.</para>
///
/// <para>🚨 Do NOT put this on the <c>[Description]</c> attributes that describe LLM TOOL
/// PARAMETERS (<c>MeshPlugin</c>, <c>McpMeshPlugin</c>, <c>Plugins/*</c>). Those are model-facing,
/// not user-facing; translating them degrades tool-calling. The completeness test excludes them by
/// design.</para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface
    | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum
    | AttributeTargets.Method | AttributeTargets.Parameter,
    AllowMultiple = true,
    Inherited = true)]
public sealed class TranslationAttribute(string locale, string text) : Attribute
{
    /// <summary>
    /// BCP-47 tag this translation is for (e.g. <c>de</c>). Matched against the viewer's resolved
    /// locale with the same primary-subtag fallback the catalog uses, so a <c>de</c> translation
    /// serves a <c>de-CH</c> viewer.
    /// </summary>
    public string Locale { get; } = locale;

    /// <summary>The translated user-visible text.</summary>
    public string Text { get; } = text;
}
