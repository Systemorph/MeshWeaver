namespace MeshWeaver.DataSetReader.Excel.Utils
{
    /// <summary>
    /// Regular-expression patterns used to sanitise column names and to recognise Excel number-format codes
    /// (double, currency/float, date and time formats).
    /// </summary>
    public static class ExcelConstants
    {
        #region regex
        //would transform " @ % 1Va\1l ueD#ata" into "ValueData"
        /// <summary>Pattern matching characters to strip when normalising a raw header into a valid column name.</summary>
        public const string NamingPattern = @"^[\d\s]+|[\\~#%'""$^+№&*{}():.;@<>?|\s-]";
        /// <summary>Pattern matching a run of digits.</summary>
        public const string NumberPattern = @"\d+";
        /// <summary>Pattern matching a name that ends with one or more digits (e.g. a duplicate-disambiguating suffix).</summary>
        public const string NumberAtEndPattern = @"^[A-Za-z]*[0-9]{1,}$";

        /// <summary>Pattern recognising number-format codes that represent floating-point / percentage / scientific values.</summary>
        public const string DoubleNumberingFormatPattern =
            @"(0{1,20}(%))|(((#{1,20})|(0{1,20}))(\.|\,)((#{0,20})|(0{0,20})))|(\d{1,}(e\+|e-)\d{0,})|(((#{0,20})|(0{0,20}))(\.|\,)((#{1,20})|(0{1,20})))";

        //Currency
        /// <summary>Pattern recognising currency-style number-format codes.</summary>
        public const string FloatNumberingFormatPattern =
            @"(.{0,1}(#{1,20})(,)(#{1,20})(0{1,20})(.)(0{1,20}))";
        /// <summary>Pattern recognising date number-format codes (day/month/year combinations).</summary>
        public const string DateNumberingFormatPattern =
            @"((m{1,5})|(d{1,5})|(y{1,5}))(\/|\-|\\-)((m{1,5})|(d{1,5})|(y{1,5}))(\/|\-|\\-){0,1}((m{0,5})|(d{0,5})|(y{0,5}))";
        /// <summary>Pattern recognising time number-format codes (hour/minute/second combinations).</summary>
        public const string TimeNumberingFormatPattern =
            @"((h{1,6})|(\[h\])|(m{1,6}))(\:|\.)((m{1,6})|(s{1,6}))((\:|\.){0,1})((s{0,6}))";
        #endregion
    }
}
