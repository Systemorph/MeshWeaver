using System.Globalization;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// 🚨 THE ONE reading of a package requirement (<c>PluginContent.requires</c> entries shaped
/// <c>AI@^1.21.0</c>) — its id half, its range half, and whether a version satisfies the range
/// (MeshWeaver#6067). Lives at the bottom of the dependency graph so every consumer — the module-set
/// proposal (<c>MeshWeaver.PluginCatalog.ModuleDependencyFloor</c>) and the GitSync import
/// (<c>MeshWeaver.GitSync.ModuleSyncDecision</c>) — applies the SAME rule: two call sites folding a
/// range differently either never converge or never fire, and both are silent.
///
/// <para>Shapes understood: <c>^</c> (npm caret), <c>~</c>, <c>&gt;=</c>, <c>&gt;</c>, an exact
/// <c>=x.y.z</c> or bare <c>x.y.z</c>, and an empty range or <c>*</c> (any version). Anything else
/// is UNVERIFIABLE — <see cref="Satisfies"/> answers null, which no caller reads as met or as a
/// refusal. Versions compare with <see cref="NuGetVersionComparer"/>.</para>
/// </summary>
public static class PackageRequirement
{
    /// <summary>The package id half: <c>"AI@^1.21.0"</c> → <c>"AI"</c>; empty for a blank entry.</summary>
    /// <param name="requirement">A requirement entry.</param>
    public static string DependencyId(string? requirement) =>
        string.IsNullOrWhiteSpace(requirement) ? "" : requirement.Split('@')[0].Trim();

    /// <summary>The range half: <c>"AI@^1.21.0"</c> → <c>"^1.21.0"</c>; empty when none is stated.</summary>
    /// <param name="requirement">A requirement entry.</param>
    public static string RangeOf(string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
            return "";
        var at = requirement.IndexOf('@');
        return at < 0 ? "" : requirement[(at + 1)..].Trim();
    }

    /// <summary>
    /// Whether <paramref name="version"/> satisfies <paramref name="range"/>: true / false, or null
    /// when the range is not a shape this understands (unverifiable, never a verdict). An empty range
    /// is met by any version. Pure.
    /// </summary>
    /// <param name="range">The constraint half of a requirement (<c>^1.21.0</c>), or empty.</param>
    /// <param name="version">The dependency's version.</param>
    public static bool? Satisfies(string? range, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var constraint = range?.Trim() ?? "";
        if (constraint.Length == 0 || constraint == "*")
            return true;
        var comparer = NuGetVersionComparer.Instance;
        if (constraint.StartsWith(">=", StringComparison.Ordinal))
            return Parse(constraint[2..]) is { } floor ? comparer.Compare(version, floor.Text) >= 0 : null;
        if (constraint.StartsWith('>'))
            return Parse(constraint[1..]) is { } floor ? comparer.Compare(version, floor.Text) > 0 : null;
        if (constraint.StartsWith('^') || constraint.StartsWith('~'))
        {
            if (Parse(constraint[1..]) is not { } floor)
                return null;
            var ceiling = constraint[0] == '^'
                ? floor.Major > 0 ? $"{floor.Major + 1}.0.0"
                : floor.Minor > 0 ? $"0.{floor.Minor + 1}.0"
                : $"0.0.{floor.Patch + 1}"
                : $"{floor.Major}.{floor.Minor + 1}.0";
            return comparer.Compare(version, floor.Text) >= 0 && comparer.Compare(version, ceiling) < 0;
        }
        var exact = constraint.StartsWith('=') ? constraint[1..] : constraint;
        return Parse(exact) is { } pinned
            ? comparer.Compare(version, pinned.Text) == 0
            : null;
    }

    private sealed record ParsedVersion(string Text, int Major, int Minor, int Patch);

    private static ParsedVersion? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        var core = trimmed;
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
            core = core[..cut];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3)
            return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        return new ParsedVersion(trimmed, numbers[0], numbers[1], numbers[2]);
    }
}
