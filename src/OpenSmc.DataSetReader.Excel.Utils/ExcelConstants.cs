namespace OpenSmc.DataSetReader.Excel.Utils
{
    public static class ExcelConstants
    {
        #region regex
        //would transform " @ % 1Va\1l ueD#ata" into "ValueData"
        public const string NamingPattern = @"^[\d\s]+|[\\~#%'""$^+№&*{}():.;@<>?|\s-]";
        public const string NumberPattern = @"\d+";
        public const string NumberAtEndPattern = @"^[A-Za-z]*[0-9]{1,}$";

        public const string DoubleNumberingFormatPattern =
            @"(0{1,20}(%))|(((#{1,20})|(0{1,20}))(\.|\,)((#{0,20})|(0{0,20})))|(\d{1,}(e\+|e-)\d{0,})|(((#{0,20})|(0{0,20}))(\.|\,)((#{1,20})|(0{1,20})))";

        //Currency
        public const string FloatNumberingFormatPattern =
            @"(.{0,1}(#{1,20})(,)(#{1,20})(0{1,20})(.)(0{1,20}))";
        public const string DateNumberingFormatPattern =
            @"((m{1,5})|(d{1,5})|(y{1,5}))(\/|\-|\\-)((m{1,5})|(d{1,5})|(y{1,5}))(\/|\-|\\-){0,1}((m{0,5})|(d{0,5})|(y{0,5}))";
        public const string TimeNumberingFormatPattern =
            @"((h{1,6})|(\[h\])|(m{1,6}))(\:|\.)((m{1,6})|(s{1,6}))((\:|\.){0,1})((s{0,6}))";
        #endregion
    }
}
