using System.Globalization;

namespace MeshWeaver.Northwind.ViewModel
{
    public static class NumberExtensions
    {
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

        public static string ToSuffixFormat(this int num) =>
            ((double)num).ToSuffixFormat("0");
    }
}
