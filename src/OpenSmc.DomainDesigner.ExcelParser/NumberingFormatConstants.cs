namespace OpenSmc.DomainDesigner.ExcelParser
{
    public class NumberingFormatConstants
    {

        public static readonly int[] IntFormatting = [1, 3, 37, 38, 59, 61];

        public static readonly int[] DoubleFormatting = [2, 4, 9, 10, 12, 13, 39, 40, 60, 62, 67, 68, 69, 70];

        public static readonly int[] FloatFormatting = [5, 6, 7, 8, 41, 42, 43, 44, 63, 64, 65, 66];

        public static readonly int[] ExponentialFormatting = [11, 48];

        public static readonly int[] DateFormatting = Enumerable.Range(14, 22 - 14)
                                                                 .Concat(Enumerable.Range(27, 36 - 27))
                                                                 .Concat(Enumerable.Range(45, 47 - 45))
                                                                 .Concat(Enumerable.Range(50, 58 - 50))
                                                                 .Concat(Enumerable.Range(71, 81 - 71))
                                                                 .ToArray();
    }
}
