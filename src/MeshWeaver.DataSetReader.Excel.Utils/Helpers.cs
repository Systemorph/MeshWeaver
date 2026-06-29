using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshWeaver.DataSetReader.Excel.Utils
{
    /// <summary>
	/// Helpers class
	/// </summary>
	public static class Helpers
    {
#if CF_DEBUG || CF_RELEASE

		/// <summary>
		/// Determines whether [is single byte] [the specified encoding].
		/// </summary>
		/// <param name="encoding">The encoding.</param>
		/// <returns>
		/// 	<c>true</c> if [is single byte] [the specified encoding]; otherwise, <c>false</c>.
		/// </returns>
		public static bool IsSingleByteEncoding(Encoding encoding)
		{
			return encoding.GetChars(new byte[] { 0xc2, 0xb5 }).Length == 1;
		}
#else

        /// <summary>
        /// Determines whether [is single byte] [the specified encoding].
        /// </summary>
        /// <param name="encoding">The encoding.</param>
        /// <returns>
        /// 	<c>true</c> if [is single byte] [the specified encoding]; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsSingleByteEncoding(Encoding encoding)
        {
            return encoding.IsSingleByte;
        }
#endif

        /// <summary>Reinterprets the bit pattern of a 64-bit integer as a <see cref="double"/>.</summary>
        /// <param name="value">The 64-bit integer holding the raw bits.</param>
        /// <returns>The double value with the same bit representation.</returns>
        public static double Int64BitsToDouble(long value)
        {
            return BitConverter.ToDouble(BitConverter.GetBytes(value), 0);
        }

        private static readonly Regex Re = new Regex("_x([0-9A-F]{4,4})_", RegexOptions.Compiled);

        /// <summary>Replaces Excel <c>_xHHHH_</c> escape sequences in shared-string text with the corresponding characters.</summary>
        /// <param name="input">The raw shared-string text, or <c>null</c>.</param>
        /// <returns>The unescaped text, or <c>null</c> if <paramref name="input"/> was <c>null</c>.</returns>
        public static string? ConvertEscapeChars(string? input)
        {
            return input == null ? null : Re.Replace(input, m => (((char)UInt32.Parse(m.Groups[1].Value, NumberStyles.HexNumber))).ToString());
        }

        /// <summary>Converts an OLE-automation date serial number to a <see cref="DateTime"/>, correcting the Excel 1900 leap-year quirk.</summary>
        /// <param name="value">The OLE-automation date serial number.</param>
        /// <returns>The corresponding <see cref="DateTime"/> boxed as <see cref="object"/>.</returns>
        public static object ConvertFromOATime(double value)
        {
            if ((value >= 0.0) && (value < 60.0))
            {
                value++;
            }
            //if (date1904)
            //{
            //    Value += 1462.0;
            //}
            return DateTime.FromOADate(value);
        }

        /// <summary>Narrows each column whose non-null cells share a single CLR type, rebuilding affected tables with the stronger column types.</summary>
        /// <param name="dataset">The dataset whose tables are inspected and, where possible, retyped in place.</param>
        public static void FixDataTypes(DataSet dataset)
        {
            var tables = new List<DataTable>(dataset.Tables.Count);
            bool convert = false;
            foreach (DataTable table in dataset.Tables)
            {

                if (table.Rows.Count == 0)
                {
                    tables.Add(table);
                    continue;
                }
                DataTable? newTable = null;
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    Type? type = null;
                    foreach (DataRow row in table.Rows)
                    {
                        if (row.IsNull(i))
                            continue;
                        var curType = row[i].GetType();
                        if (curType != type)
                        {
                            if (type == null)
                                type = curType;
                            else
                            {
                                type = null;
                                break;
                            }
                        }
                    }
                    if (type != null)
                    {
                        convert = true;
                        if (newTable == null)
                            newTable = table.Clone();
                        newTable.Columns[i].DataType = type;

                    }
                }
                if (newTable != null)
                {
                    newTable.BeginLoadData();
                    foreach (DataRow row in table.Rows)
                    {
                        newTable.ImportRow(row);
                    }

                    newTable.EndLoadData();
                    tables.Add(newTable);

                }
                else tables.Add(table);
            }
            if (convert)
            {
                dataset.Tables.Clear();
                dataset.Tables.AddRange(tables.ToArray());
            }
        }

        /// <summary>Adds a column to a table, appending a <c>_n</c> suffix to keep the name unique when it already exists.</summary>
        /// <param name="table">The table to add the column to.</param>
        /// <param name="columnName">The desired column name.</param>
        public static void AddColumnHandleDuplicate(DataTable table, string columnName)
        {
            //if a colum  already exists with the name append _i to the duplicates
            var adjustedColumnName = columnName;
            var column = table.Columns[columnName];
            var i = 1;
            while (column != null)
            {
                adjustedColumnName = string.Format("{0}_{1}", columnName, i);
                column = table.Columns[adjustedColumnName];
                i++;
            }

            table.Columns.Add(adjustedColumnName, typeof(Object));
        }

        /// <summary>Strips every character matched by the supplied naming pattern from a value.</summary>
        /// <param name="value">The raw value to sanitise.</param>
        /// <param name="namingPattern">The regular-expression pattern describing characters to remove.</param>
        /// <returns>The value with all matched characters removed.</returns>
        public static string MatchRegexNamingPattern(string value, string namingPattern)
        {
            return Regex.Replace(value, namingPattern, string.Empty);
        }
    }
}
