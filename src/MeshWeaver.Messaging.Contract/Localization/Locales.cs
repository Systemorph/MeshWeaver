using System.Collections.Immutable;

namespace MeshWeaver.Messaging;

/// <summary>
/// The languages this deployment ships, and the ONE rule for resolving an arbitrary requested
/// language tag to one of them. Pure, dependency-free and deterministic.
///
/// <para>Both localization shapes route through here — the string catalog (Blazor markup, inline
/// <c>Controls.*</c> literals, toasts) and the <c>[Translation]</c> attributes (property labels,
/// node-type names, enum members) — so they can never disagree about which language a viewer is in.
/// Placed in the contract layer, below <c>AccessService</c>, so the hub, the mesh, the layout areas
/// and the Blazor client all share one definition.</para>
/// </summary>
public static class Locales
{
    /// <summary>
    /// The fallback language. Every unsupported, empty or unresolvable tag lands here, so a missing
    /// translation degrades to readable English rather than to a blank UI.
    /// </summary>
    public const string Default = "en";

    /// <summary>
    /// The languages this deployment ships translations for, in display order. Adding a language is
    /// this list, plus a <c>strings.{tag}.json</c> catalog, plus the matching
    /// <c>[Translation("{tag}", …)]</c> attributes — nothing else.
    /// </summary>
    public static ImmutableList<string> Supported { get; } = ["en", "de"];

    /// <summary>
    /// Endonyms (each language named in itself) for the settings picker — a German speaker looks
    /// for "Deutsch", not "German". Keyed by the tag stored in <c>User.Locale</c>.
    /// </summary>
    public static ImmutableDictionary<string, string> DisplayNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["de"] = "Deutsch",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves an arbitrary requested tag to a SUPPORTED language, never throwing and never
    /// returning null. Falls back in three steps: exact match → primary subtag (so <c>de-CH</c>,
    /// <c>de-AT</c> and <c>de_DE</c> all serve German) → <see cref="Default"/>.
    /// </summary>
    public static string Resolve(string? requested) => TryMatch(requested) ?? Default;

    /// <summary>
    /// Returns the supported tag matching <paramref name="requested"/>, or <c>null</c> when this
    /// deployment ships no translation for it. Use <see cref="Resolve"/> when you need a usable
    /// tag; use this when "unsupported" must stay distinguishable from "English" — the write-once
    /// profile population needs that distinction so it does not pin a profile to a language we
    /// would only ever render in English anyway.
    /// </summary>
    public static string? TryMatch(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        // Accept BCP-47 ("de-CH"), POSIX ("de_CH.UTF-8") and weighted Accept-Language
        // ("de-CH;q=0.9") shapes — the browser, the OS and the HTTP header each produce a
        // different one and they all arrive at this method.
        var tag = requested.Trim().Split('.', ';')[0].Replace('_', '-');

        foreach (var supported in Supported)
            if (string.Equals(supported, tag, StringComparison.OrdinalIgnoreCase))
                return supported;

        var primary = tag.Split('-')[0];
        if (primary.Length == 0)
            return null;

        foreach (var supported in Supported)
            if (string.Equals(supported, primary, StringComparison.OrdinalIgnoreCase))
                return supported;

        return null;
    }
}
