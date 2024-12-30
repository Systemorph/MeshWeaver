using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using System;

namespace MeshWeaver.Northwind.ViewModel
{
    /// <summary>
    /// Provides extension methods for the annual report.
    /// </summary>
    public static class AnnualReportExtensions
    {
        /// <summary>
        /// Gets the year from the query string parameters of the layout area.
        /// </summary>
        /// <param name="layoutArea">The layout area host.</param>
        /// <returns>The year as an integer. If the year is not specified or invalid, returns the current year.</returns>
        public static int Year(this LayoutAreaHost layoutArea)
        {
            var yearString = layoutArea.GetQueryStringParamValue(nameof(Year));

            if (!string.IsNullOrEmpty(yearString) && int.TryParse(yearString, out var year))
            {
                return year;
            }

            return 2023;
        }
    }
}
