namespace MeshWeaver.Yaml;

/// <summary>
/// The ONE exception the YAML reader throws for text it cannot read. It names the position
/// (1-based line and column) where reading stopped, so a caller can report the failure or fall
/// back to a narrower reader of its own.
/// </summary>
public sealed class YamlParseException : FormatException
{
    /// <summary>Creates the exception for <paramref name="reason"/> at the given position.</summary>
    /// <param name="reason">What was wrong, without the position.</param>
    /// <param name="line">The 1-based line at which reading stopped.</param>
    /// <param name="column">The 1-based column at which reading stopped.</param>
    public YamlParseException(string reason, int line, int column)
        : base($"{reason} (line {line}, column {column})")
    {
        Reason = reason;
        Line = line;
        Column = column;
    }

    /// <summary>What was wrong, without the position.</summary>
    public string Reason { get; }

    /// <summary>The 1-based line at which reading stopped.</summary>
    public int Line { get; }

    /// <summary>The 1-based column at which reading stopped.</summary>
    public int Column { get; }
}

/// <summary>
/// How strictly the reader holds the text to the YAML specification. The default is TOLERANT:
/// where malformed text has exactly one sensible reading, it is read that way.
/// </summary>
public sealed record YamlParseOptions
{
    /// <summary>
    /// When <c>true</c>, the text is held to the YAML grammar where the tolerant reader would
    /// accept it: a <c>": "</c> inside a plain scalar value and a duplicate mapping key are refused
    /// with a <see cref="YamlParseException"/>. Use it to check that a file is readable by OTHER
    /// (strict) YAML readers; the platform's own reads are tolerant.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>The default: tolerant reading.</summary>
    public static YamlParseOptions Tolerant { get; } = new();

    /// <summary>Spec-strict reading — see <see cref="Strict"/>.</summary>
    public static YamlParseOptions StrictSpec { get; } = new() { Strict = true };
}
