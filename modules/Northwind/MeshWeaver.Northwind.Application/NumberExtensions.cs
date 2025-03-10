using System.Globalization;

namespace MeshWeaver.Northwind.Application
{
    /// <summary>
    /// Provides extension methods for formatting numbers with suffixes.
    /// </summary>
    public static class NumberExtensions
    {
        /// <summary>
        /// Converts a double to a string with a suffix format (e.g., K for thousands, M for millions, B for billions).
        /// </summary>
        /// <param name="num">The number to format.</param>
        /// <param name="format">The format string to use if no suffix is applied. Default is "0.##".</param>
        /// <returns>A string representing the formatted number with the appropriate suffix.</returns>
        public static string ToSuffixFormat(this double num, string format = "0.##")
        {
            switch (num)
            {
                case > 999999999:
                case < -999999999:
                    return num.ToString("0,,,.###B", CultureInfo.InvariantCulture);
                case > 999999:
                case < -999999:
                    return num.ToString("0,,.##M", CultureInfo.InvariantCulture);
                case > 999:
                case < -999:
                    return num.ToString("0,.#K", CultureInfo.InvariantCulture);
                default:
                    return num.ToString(format, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Converts an integer to a string with a suffix format (e.g., K for thousands, M for millions, B for billions).
        /// </summary>
        /// <param name="num">The number to format.</param>
        /// <returns>A string representing the formatted number with the appropriate suffix.</returns>
        public static string ToSuffixFormat(this int num) =>
            ((double)num).ToSuffixFormat("0");
    }
}
