using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.ViewModel
{
    public static class AnnualReportExtensions
    {
        public static int Year(this LayoutAreaHost layoutArea)
        {
            var yearString = layoutArea.GetQueryStringParamValue(nameof(Year));

            if (!string.IsNullOrEmpty(yearString) && int.TryParse(yearString, out var year))
            {
                return year;
            }

            return DateTime.Now.Year;
        }
    }
}
