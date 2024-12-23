using System.Text.RegularExpressions;

namespace MeshWeaver.Charting.Models.Options;

public static class ChartHelpers
{
    public static void CheckPalette(string[] values)
    {
        foreach (var color in values)
            if (!Regex.IsMatch(color, "^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$"))
                throw new ArgumentException(
                    $"Invalid color '{color}' in array. Colors have to be specified as a string like '#00ffa1' or '#cfa' (starting with # and 6 or 3 hexadecimal values for RGB)."
                );
    }
}
