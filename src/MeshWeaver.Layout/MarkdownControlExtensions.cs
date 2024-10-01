using System.Reflection;
using System.Text;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout
{
    /// <summary>
    /// Provides extension methods for working with <see cref="MarkdownControl"/>.
    /// </summary>
    public static class MarkdownControlExtensions
    {
        /// <summary>
        /// Converts the specified items to a markdown table.
        /// </summary>
        /// <typeparam name="T">The type of the items.</typeparam>
        /// <param name="items">The items to convert to a markdown table.</param>
        /// <returns>A <see cref="MarkdownControl"/> containing the markdown table.</returns>
        public static MarkdownControl ToMarkdown<T>(this IEnumerable<T> items) 
            => Controls.Markdown(FormatAsTable(items));

        /// <summary>
        /// Formats the specified items as a markdown table.
        /// </summary>
        /// <typeparam name="T">The type of the items.</typeparam>
        /// <param name="items">The items to format as a markdown table.</param>
        /// <returns>A string containing the markdown table.</returns>
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
}
