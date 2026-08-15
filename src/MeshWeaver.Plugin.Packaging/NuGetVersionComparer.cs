using System.Globalization;

namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// Orders NuGet/SemVer-2 version strings.
///
/// <para>🚨 <b>String ordering is wrong here in a way that silently picks an old build.</b> The
/// framework's continuous versions are <c>3.0.0-rc3.ci.&lt;run-number&gt;</c>, and as text
/// <c>"3.0.0-rc3.ci.900"</c> sorts ABOVE <c>"3.0.0-rc3.ci.3758"</c> — `9` &gt; `3`. Picking the
/// "latest" that way compiles every plugin against a framework thousands of runs stale, and nothing
/// reports it: the build succeeds, because that framework is a real one.</para>
///
/// <para>SemVer's rule is what avoids it: dot-separated pre-release identifiers compare
/// field-by-field, numerically when both are numeric. Build metadata (<c>+build.123</c>) is ignored
/// for ordering, per the spec.</para>
/// </summary>
public sealed class NuGetVersionComparer : IComparer<string>
{
    /// <summary>Shared instance — stateless.</summary>
    public static readonly NuGetVersionComparer Instance = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (leftCore, leftPre) = Split(x);
        var (rightCore, rightPre) = Split(y);

        var core = CompareCore(leftCore, rightCore);
        if (core != 0)
            return core;

        // A version WITHOUT a pre-release outranks one with: 3.0.0 > 3.0.0-rc3.
        if (leftPre.Length == 0 && rightPre.Length == 0) return 0;
        if (leftPre.Length == 0) return 1;
        if (rightPre.Length == 0) return -1;

        return ComparePreRelease(leftPre, rightPre);
    }

    /// <summary>Splits into numeric core and pre-release, discarding build metadata.</summary>
    private static (string Core, string[] PreRelease) Split(string version)
    {
        var plus = version.IndexOf('+');
        if (plus >= 0)
            version = version[..plus];

        var dash = version.IndexOf('-');
        return dash < 0
            ? (version, [])
            : (version[..dash], version[(dash + 1)..].Split('.'));
    }

    private static int CompareCore(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');

        for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            var l = i < leftParts.Length && int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var lv) ? lv : 0;
            var r = i < rightParts.Length && int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rv) ? rv : 0;
            if (l != r)
                return l.CompareTo(r);
        }
        return 0;
    }

    private static int ComparePreRelease(string[] left, string[] right)
    {
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            // Fewer identifiers ranks LOWER when all preceding are equal: rc3 < rc3.ci.1.
            if (i >= left.Length) return -1;
            if (i >= right.Length) return 1;

            var leftNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var lv);
            var rightNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rv);

            // THE case this type exists for: both numeric compares as NUMBERS, so ci.3758 > ci.900.
            if (leftNumeric && rightNumeric)
            {
                if (lv != rv) return lv.CompareTo(rv);
                continue;
            }

            // Numeric identifiers always rank lower than alphanumeric ones (SemVer §11.4.3).
            if (leftNumeric) return -1;
            if (rightNumeric) return 1;

            var text = string.CompareOrdinal(left[i], right[i]);
            if (text != 0)
                return text < 0 ? -1 : 1;
        }
        return 0;
    }
}
