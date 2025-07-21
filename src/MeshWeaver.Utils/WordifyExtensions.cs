#nullable enable
using System.Text.RegularExpressions;

namespace MeshWeaver.Utils;

public static class WordifyExtensions
{
    private static readonly Regex WordifyRegex = new("(?<=[a-z])(?<x>[A-Z])|(?<=.)(?<x>[A-Z])(?=[a-z])");
    private static readonly Regex RedundantWhitespaceRegex = new("[ ]{2,}");

    /// <summary>
    /// Taken from https://stackoverflow.com/questions/59811115/fail-to-execute-dotnet-tool-restore-interactive-in-build-pipeline-when-using
    /// Add spaces to separate the capitalized words in the string, 
    /// i.e. insert a space before each uppercase letter that is 
    /// either preceded by a lowercase letter or followed by a 
    /// lowercase letter (but not for the first char in string). 
    /// This keeps groups of uppercase letters - e.g. acronyms - together.
    /// </summary>
    /// <param name="pascalCaseString">A string in PascalCase</param>
    /// <returns></returns>
    public static string Wordify(this string pascalCaseString)
    {
        var wordified = WordifyRegex.Replace(pascalCaseString, " ${x}");
        return RedundantWhitespaceRegex.Replace(wordified, " ");
    }

    public static string RemoveWhiteSpaces(this string displayName)
    {
        return displayName.Replace(" ", "");
    }
}