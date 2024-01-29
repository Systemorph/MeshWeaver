using System.Text.RegularExpressions;
using OpenSmc.DataSetReader.Excel.Utils;

namespace OpenSmc.DomainDesigner.ExcelParser.DocumentParsing
{
    public static class NumberingFormatDecoder 
    {
        public static Type Decode(string formatCode)
        {
            if (string.IsNullOrEmpty(formatCode))
                return typeof(string);

            if (Regex.IsMatch(formatCode, ExcelConstants.DateNumberingFormatPattern, RegexOptions.IgnoreCase)
                || Regex.IsMatch(formatCode, ExcelConstants.TimeNumberingFormatPattern, RegexOptions.IgnoreCase))
            {
                return typeof(DateTime);
            }
            if (Regex.IsMatch(formatCode, ExcelConstants.FloatNumberingFormatPattern, RegexOptions.IgnoreCase))
            {
                return typeof(float);
            }
            if (Regex.IsMatch(formatCode, ExcelConstants.DoubleNumberingFormatPattern, RegexOptions.IgnoreCase)
                || formatCode.Last() == '%')
            {
                return typeof(double);
            }
            return typeof(string);
        }

        public static Type Decode(int styleValue)
        {
            if (styleValue == 49u || styleValue == 0)
            {
                return typeof(string);
            }
            if (NumberingFormatConstants.IntFormatting.Contains(styleValue))
            {
                return typeof(int);
            }
            if (NumberingFormatConstants.DoubleFormatting.Contains(styleValue))
            {
                return typeof(double);
            }
            if (NumberingFormatConstants.FloatFormatting.Contains(styleValue))
            {
                return typeof(float);
            }
            if (NumberingFormatConstants.ExponentialFormatting.Contains(styleValue))
            {
                return typeof(double);
            }
            if (NumberingFormatConstants.DateFormatting.Contains(styleValue))
            {
                return typeof(DateTime);
            }

            return typeof(string);
        }
    }
}
