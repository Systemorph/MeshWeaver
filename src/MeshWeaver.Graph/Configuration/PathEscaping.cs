namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Utility for escaping and unescaping paths containing illegal file system characters.
/// </summary>
public static class PathEscaping
{
    private const string Separator = "__";
    private const string ForwardSlash = "/";
    private const string BackSlash = "\\";

    /// <summary>
    /// Escapes illegal file system characters in an ID for use as a file path.
    /// Replaces "/" and "\" with "__".
    /// </summary>
    public static string Escape(string id)
    {
        if (string.IsNullOrEmpty(id))
            return id;

        return id
            .Replace(ForwardSlash, Separator)
            .Replace(BackSlash, Separator);
    }

    /// <summary>
    /// Unescapes a file path back to the original ID.
    /// Replaces "__" with "/".
    /// </summary>
    public static string Unescape(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        return path.Replace(Separator, ForwardSlash);
    }
}
