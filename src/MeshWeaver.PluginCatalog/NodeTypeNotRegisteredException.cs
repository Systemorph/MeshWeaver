using System.Text.RegularExpressions;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An install write was refused because the node's NodeType is not registered on the host:
/// <c>NodeType 'X' is not registered</c>. Typed so the default-install lane classifies it by TYPE
/// (a closed type set holds such a package, <see cref="InstanceAutoRegistrationService"/>) rather
/// than by matching text anywhere in an arbitrary exception.
///
/// <para>The refusal crosses the message bus as the owning hub's error STRING
/// (<c>CreateOrUpdateNodeResponse.Error</c>), so the one place that text is read is
/// <see cref="TryFrom"/>, at the install's write boundary, where the string is known to be the hub's
/// own refusal of that write.</para>
/// </summary>
public sealed class NodeTypeNotRegisteredException : InvalidOperationException
{
    private static readonly Regex Refusal = new(
        @"NodeType '([^']+)' is not registered",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The full install failure message.</param>
    /// <param name="typeNames">The unregistered NodeTypes the refusal names.</param>
    public NodeTypeNotRegisteredException(string message, IReadOnlyList<string> typeNames) : base(message)
    {
        TypeNames = typeNames;
    }

    /// <summary>The unregistered NodeTypes the refusal names, distinct, in order of appearance.</summary>
    public IReadOnlyList<string> TypeNames { get; }

    /// <summary>
    /// Reads the hub's refusal of the write at <paramref name="path"/>: a typed exception when
    /// <paramref name="error"/> names unregistered NodeTypes, otherwise <c>null</c>.
    /// </summary>
    /// <param name="path">The path the write targeted.</param>
    /// <param name="error">The owning hub's error string.</param>
    public static NodeTypeNotRegisteredException? TryFrom(string path, string? error)
    {
        if (string.IsNullOrEmpty(error))
            return null;
        var names = Refusal.Matches(error).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        return names.Count == 0
            ? null
            : new NodeTypeNotRegisteredException($"Install of '{path}' failed: {error}", names);
    }
}
