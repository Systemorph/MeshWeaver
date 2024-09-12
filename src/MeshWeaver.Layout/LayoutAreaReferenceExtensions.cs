namespace MeshWeaver.Layout;

public static class LayoutAreaReferenceExtensions
{
    public static IReadOnlyDictionary<string, string> GetQueryStringParams(this LayoutAreaReference layoutAreaReference)
        => ParseQueryString((string)layoutAreaReference.Id);

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var queryParameters = new Dictionary<string, string>();

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
                    queryParameters[key] = value;
                }
            }
        }

        return queryParameters;
    }
}
