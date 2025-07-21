#nullable enable
using System.Collections;
using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MeshWeaver.Activities
{
    public static class ActivityLogHtmlBuilder
    {
        public static string GetActivityLog(ActivityLog log)
        {
            var htmlParser = new HtmlParser();
            var document = htmlParser.ParseDocument("");
            var logElement = document.CreateElement("div");
            var tableFlat = document.CreateElement("table");

            var row = document.CreateElement("tr");

            var color = "black";
            if (log.Status == ActivityStatus.Failed)
                color = "red";
            if (log.Status == ActivityStatus.Succeeded)
                color = "green";
            if (log.Status == ActivityStatus.Cancelled)
                color = "grey";

            var statusCol2Font = document.CreateElement("font");
            statusCol2Font.SetAttribute("color", color);
            var statusCol2Bold = document.CreateElement("b");
            statusCol2Bold.TextContent = log.Status.ToString();
            statusCol2Font.AppendChild(statusCol2Bold);

            AddColumns(document, row, $"{nameof(ActivityLog.Status)}:", log.Status.ToString(), statusCol2Font);
            AddColumns(document, row, $"{nameof(ActivityLog.Start).Replace(nameof(DateTime), "")}:", log.Start.ToString(CultureInfo.InvariantCulture));
            AddColumns(document, row, $"{nameof(ActivityLog.End).Replace(nameof(DateTime), "")}:", log.End?.ToString(CultureInfo.InvariantCulture));
            AddColumns(document, row, "ActivityId", log.Id);
            if (log.User != null)
                AddColumns(document, row, nameof(ActivityLog.User), log.User.DisplayName);

            tableFlat.AppendChild(row);

            var tableLists = document.CreateElement("table");

            GetRowForNotifications(document, tableLists, nameof(ActivityLogExtensions.Errors), log.Errors(), "background-color: MistyRose; border:2px Red solid;");
            GetRowForNotifications(document, tableLists, nameof(ActivityLogExtensions.Warnings), log.Warnings(), "background-color: LemonChiffon; border:2px Gold solid;");
            GetRowForNotifications(document, tableLists, nameof(ActivityLogExtensions.Infos), log.Infos(), "background-color: LightCyan; border:2px SkyBlue solid;");

            logElement.AppendChild(tableFlat);
            logElement.AppendChild(tableLists);
            return logElement.OuterHtml;
        }

        private static void AddColumns(IDocument document, IElement row, string name, string? value, IElement? child = null)
        {
            var nameCol = document.CreateElement("td");
            nameCol.SetAttribute("style", "padding-right: 5px;");

            var valueCol = document.CreateElement("td");
            valueCol.SetAttribute("style", "padding-left: 5px;");

            nameCol.TextContent = name;
            if (child != null)
                valueCol.AppendChild(child);
            else
                valueCol.TextContent = value ?? "";
            row.AppendChild(nameCol);
            row.AppendChild(valueCol);
        }

        private static void GetRowForNotifications(IDocument document, IElement table, string name, IReadOnlyCollection<LogMessage> items, string style)
        {
            if (items.Count == 0)
                return;

            var row = document.CreateElement("tr");

            var nameCol = document.CreateElement("td");
            nameCol.SetAttribute("style", "padding-top: 25px;");
            var valueCol = document.CreateElement("td");
            nameCol.TextContent = $"{name} ({items.Count})";

            var list = document.CreateElement("ul");
            list.SetAttribute("style", $@"{style} padding: 10px 30px 10px 30px; margin: 0px;");

            foreach (var item in items)
            {
                var listElement = document.CreateElement("il");
                listElement.SetAttribute("style", "display: list-item");

                var type = item.GetType();
                var properties = type.GetProperties().Select(x =>
                {
                    object? value = x.GetValue(item);


                    if (value is IEnumerable enumerable && x.PropertyType != typeof(string))
                    {
                        var propertyList = new List<string>();
                        foreach (var property in enumerable)
                            propertyList.Add(property?.ToString() ?? "");
                        value = string.Join(',', propertyList);
                    }

                    return $"{x.Name} = {value}";
                });
                listElement.TextContent = $"{string.Join(", ", properties)}";
                list.AppendChild(listElement);
            }

            valueCol.AppendChild(list);

            row.AppendChild(nameCol);
            row.AppendChild(valueCol);

            table.AppendChild(row);
        }
    }
}
