using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides extension methods for working with layout area query strings.
/// </summary>
public static class LayoutAreaQueryString
{
    private const string QueryStringParams = nameof(QueryStringParams);

    /// <summary>
    /// Gets the query string parameters for the specified layout area.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <returns>A read-only collection of key-value pairs representing the query string parameters.</returns>
    public static IReadOnlyCollection<KeyValuePair<string, string>>? GetQueryStringParams(this LayoutAreaHost layoutArea)
        => layoutArea.GetOrAddVariable(QueryStringParams, () => Parse((string?)layoutArea.Reference.Id ?? ""));

    /// <summary>
    /// Gets the value of the specified query string parameter for the layout area.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="key">The key of the query string parameter.</param>
    /// <returns>The value of the query string parameter, or <c>null</c> if the parameter is not found.</returns>
    public static string? GetQueryStringParamValue(this LayoutAreaHost layoutArea, string key)
        => layoutArea.GetQueryStringParams()
            ?.FirstOrDefault(x => x.Key == key).Value;
    
    /// <summary>
    /// Gets the values of the specified query string parameter for the layout area.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="key">The key of the query string parameter.</param>
    /// <returns>A read-only collection of values for the query string parameter.</returns>
    public static IReadOnlyCollection<string> GetQueryStringParamValues(this LayoutAreaHost layoutArea, string key)
        => layoutArea.GetQueryStringParams()
            ?.Where(x => x.Key == key)
            .Select(x => x.Value)
            .ToArray() ?? Array.Empty<string>();

    /// <summary>
    /// Parses the specified query string into a collection of key-value pairs.
    /// </summary>
    /// <param name="queryString">The query string to parse.</param>
    /// <returns>A read-only collection of key-value pairs representing the parsed query string parameters.</returns>
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
