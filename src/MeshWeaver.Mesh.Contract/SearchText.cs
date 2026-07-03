using System.Globalization;
using System.Text;

namespace MeshWeaver.Mesh;

/// <summary>
/// Diacritic-insensitive, case-insensitive text matching for in-process search filtering —
/// the matcher behind <c>MeshNodePickerView</c>'s client-side filter. Folds combining marks
/// (ü→u, é→e) plus the common Latin ligatures/specials FormD does not decompose (ß→ss, æ→ae,
/// œ→oe, ø→o, đ→d, ł→l), so "Burgi" finds "Bürgi" and vice versa.
/// </summary>
public static class SearchText
{
    /// <summary>
    /// Folds <paramref name="text"/> to its lower-case, diacritic-free comparison form.
    /// Null/empty input folds to <c>""</c>.
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            switch (char.ToLowerInvariant(c))
            {
                case 'ß': sb.Append("ss"); break;
                case 'æ': sb.Append("ae"); break;
                case 'œ': sb.Append("oe"); break;
                case 'ø': sb.Append('o'); break;
                case 'đ': sb.Append('d'); break;
                case 'ł': sb.Append('l'); break;
                default: sb.Append(char.ToLowerInvariant(c)); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when the folded <paramref name="searchText"/> is a substring of any folded
    /// <paramref name="fields"/> entry. An empty search text matches everything.
    /// </summary>
    public static bool Matches(string? searchText, params string?[] fields)
    {
        var needle = Fold(searchText);
        if (needle.Length == 0) return true;
        foreach (var field in fields)
        {
            if (!string.IsNullOrEmpty(field) && Fold(field).Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
