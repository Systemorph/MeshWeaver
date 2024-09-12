using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public static class LayoutAreaQueryString
{
    private const string QueryStringParams = nameof(QueryStringParams);

    public static IReadOnlyCollection<KeyValuePair<string, string>> GetQueryStringParams(this LayoutAreaHost layoutArea)
        => layoutArea.GetOrAddVariable(QueryStringParams, () => Parse((string)layoutArea.Stream.Reference.Id));

    public static string GetQueryStringParamValue(this LayoutAreaHost layoutArea, string key)
        => layoutArea.GetQueryStringParams()
            .FirstOrDefault(x => x.Key == key).Value;
    
    public static IReadOnlyCollection<string> GetQueryStringParamValues(this LayoutAreaHost layoutArea, string key)
        => layoutArea.GetQueryStringParams()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .ToArray();

    public static IReadOnlyCollection<KeyValuePair<string, string>> Parse(string queryString)
    {
        var values = new List<KeyValuePair<string, string>>();

        if (queryString is not null)
        {
            queryString = queryString.Split('?').Last();
            foreach (var pair in queryString.Split('&'))
            {
                var keyValue = pair.Split('=');

                if (keyValue.Length == 2)
                {
                    var key = Uri.UnescapeDataString(keyValue[0]);
                    var value = Uri.UnescapeDataString(keyValue[1]);
                    
                    values.Add(new (key, value));
                }
            }
        }

        return values;
    }
}
