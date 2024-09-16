using System.Reflection;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Utils;

namespace MeshWeaver.Northwind.ViewModel;

public static class MarkdownFormatting {

    public static MarkdownControl ToMarkdown<T>(this IEnumerable<T> items) 
        => Controls.Markdown(FormatAsTable(items));

    private static string FormatAsTable<T>(IEnumerable<T> items)
    {
        var itemsArray = items as T[] ?? items.ToArray();

        if (itemsArray.Length == 0)
        {
            return string.Empty;
        }

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var header = string.Join(" | ", properties.Select(p => p.Name.Wordify()));
        var separator = string.Join(" | ", properties.Select(p => "---"));
        var rows = itemsArray.Select(item => string.Join(" | ", properties.Select(p => p.GetValue(item)?.ToString() ?? string.Empty)));

        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(separator);
        foreach (var row in rows)
        {
            sb.AppendLine(row);
        }

        return sb.ToString();
    }
}
