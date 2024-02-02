using OpenSmc.Pivot.Models;
using OpenSmc.Utils;

namespace OpenSmc.Reporting.Models
{
    public static class GridBindings
    {
        private static string Getter(string propertyName)
        {
            return propertyName.ToCamelCase();
        }

        public static string Data = $"data.{Getter(nameof(GridRow.Data))}";

        public static string GetDataPath = $"data => data.{Getter(nameof(GridRow.RowGroup))}.{Getter(nameof(GridRow.RowGroup.Coordinates))}";

        public static (string Name, string Component) DisplayNameGroupColumnRenderer = ("displayNameGroupColumnRenderer", @$"(function () {{
                function DisplayNameGroupColumnRenderer() {{}}
                DisplayNameGroupColumnRenderer.prototype.init = function (params) {{
                    var tempDiv = document.createElement('div');
                    tempDiv.innerHTML = params.data.{Getter(nameof(Row.RowGroup))}.{Getter(nameof(Row.RowGroup.DisplayName))};
                    this.eGui = tempDiv.firstChild;
                }};
                DisplayNameGroupColumnRenderer.prototype.getGui = function () {{
                    return this.eGui;
                }};
                return DisplayNameGroupColumnRenderer;
        }}())");

        public static string GetRowStyle = $"params => params.data.{Getter(nameof(GridRow.Style))}";
    }

    public static class StringExtensions
    {
        public static string Accessor(this string input)
        {
            if (input.Length == 0) return "null";
            //x = Regex.Replace(x, "([A-Z])([A-Z]+)($|[A-Z])",
            //                  m => m.Groups[1].Value + m.Groups[2].Value.ToLower() + m.Groups[3].Value);
            var binderName = input.IsNumeric() ? input : "'" + input.ToCamelCase() + "'";
            binderName = "[" + binderName + "]";
            return binderName;
        }

        private static bool IsNumeric(this string input)
        {
            // can this be double? 
            // input.All(char.IsNumber) is another way, might be more efficient
            return int.TryParse(input, out _);
        }
    }
}