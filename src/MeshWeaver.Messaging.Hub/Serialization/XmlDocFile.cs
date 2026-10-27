using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// What one member's XML documentation contributes to a summary lookup, already rendered to text.
/// Immutable and free of reflection objects, so a snapshot never roots the assembly it came from.
/// </summary>
/// <param name="Summary">The rendered <c>&lt;summary&gt;</c>, or <c>null</c> when the member has none.</param>
/// <param name="InheritDoc">Whether the member carries an <c>&lt;inheritdoc/&gt;</c>.</param>
/// <param name="InheritDocCref">The <c>cref</c> of that <c>&lt;inheritdoc/&gt;</c>, if it names one.</param>
/// <param name="Params">The rendered <c>&lt;param&gt;</c> texts by name (a positional record documents its properties there).</param>
internal sealed record XmlMemberDoc(
    string? Summary,
    bool InheritDoc,
    string? InheritDocCref,
    ImmutableDictionary<string, string> Params);

/// <summary>
/// One assembly's XML documentation file, parsed ONCE into an immutable map from documentation
/// comment ID to <see cref="XmlMemberDoc"/>. The <see cref="XDocument"/> is discarded after the
/// parse; nothing that is later read concurrently is ever a live XLinq tree.
///
/// <para><b>Kept small, because every mesh holds its own.</b> The reader is mesh-owned, so a
/// process running many meshes (a test host, above all) parses the same files once per mesh. The
/// map therefore keeps only what a lookup can read: a member with no summary, no
/// <c>&lt;inheritdoc/&gt;</c> and no <c>&lt;param&gt;</c> is left out (a lookup treats it exactly
/// as absent), and <c>&lt;param&gt;</c> texts are kept on TYPE entries only — the positional-record
/// fallback is the one place they are read. The texts and IDs of an assembly that can never unload
/// are interned (<see cref="string.Intern"/>), so every mesh's map points at ONE copy of each
/// string; a collectible assembly's are not, because the runtime's intern pool would keep them
/// alive after its load context unloads. Measured on a test host before this: about 10 MB of
/// rendered text per mesh, ~835 MB across the meshes alive at once.</para>
/// </summary>
internal sealed class XmlDocFile
{
    /// <summary>A file that does not exist, could not be read, or belongs to a dynamic assembly.</summary>
    public static readonly XmlDocFile Empty = new(ImmutableDictionary<string, XmlMemberDoc>.Empty, null);

    // The params of the (vast majority of) entries that carry none: one shared immutable instance
    // instead of an empty dictionary per member.
    private static readonly ImmutableDictionary<string, string> NoParams = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private XmlDocFile(ImmutableDictionary<string, XmlMemberDoc> members, string? path)
    {
        Members = members;
        Path = path;
    }

    /// <summary>Every documented member, keyed by documentation comment ID (e.g. <c>P:Ns.T.Name</c>).</summary>
    public ImmutableDictionary<string, XmlMemberDoc> Members { get; }

    /// <summary>The file the map was read from, or <c>null</c> when none was found.</summary>
    public string? Path { get; }

    /// <summary>Locates and parses the documentation file of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>The parsed file, or <see cref="Empty"/>.</returns>
    public static XmlDocFile Load(Assembly assembly)
        => Locate(assembly) is { } path ? Parse(path, intern: !assembly.IsCollectible) : Empty;

    /// <summary>
    /// The documentation file of <paramref name="assembly"/>: <c>{AssemblyName}.xml</c> beside
    /// <see cref="Assembly.Location"/> and nowhere else; ONLY for a stream-loaded or single-file
    /// assembly with no location, the application base directory and the current directory, each
    /// also under <c>bin/</c>. The NuGet-cache and SDK-pack probing the previous third-party reader did on a
    /// developer machine is deliberately not carried over: no deployed image has those directories,
    /// so it only made a summary appear locally that production never showed.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    public static string? Locate(Assembly assembly)
    {
        if (assembly.IsDynamic || assembly.GetName().Name is not { Length: > 0 } name)
            return null;
        var file = name + ".xml";
        if (assembly.Location is { Length: > 0 } location)
        {
            // A LOCATED assembly reads only its own sidecar. Probing elsewhere could hand a
            // collectible generation another generation's same-named file.
            return System.IO.Path.GetDirectoryName(location) is { } directory
                ? Existing(System.IO.Path.Combine(directory, file))
                : null;
        }

        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            if (Existing(System.IO.Path.Combine(root, file)) is { } direct)
                return direct;
            if (Existing(System.IO.Path.Combine(root, "bin", file)) is { } underBin)
                return underBin;
        }
        return null;
    }

    private static string? Existing(string path) => System.IO.File.Exists(path) ? path : null;

    /// <summary>Parses a documentation file. An unreadable or malformed file yields <see cref="Empty"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="intern">Whether to intern the texts and IDs — only for an assembly that can never unload.</param>
    /// <returns>The parsed file.</returns>
    public static XmlDocFile Parse(string path, bool intern)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            // Absent documentation, not a fault: a doc summary is optional display metadata and the
            // file may be mid-replacement on a shared volume. The member simply has no summary.
            return Empty;
        }

        var members = ImmutableDictionary.CreateBuilder<string, XmlMemberDoc>(StringComparer.Ordinal);
        var root = document.Element("doc")?.Element("members");
        if (root is not null)
        {
            foreach (var member in root.Elements("member"))
            {
                // First wins, as a sequential search of the file would find it.
                if (member.Attribute("name")?.Value is not { Length: > 0 } id || members.ContainsKey(id))
                    continue;
                if (Describe(member, isType: id.StartsWith("T:", StringComparison.Ordinal), intern) is { } doc)
                    members.Add(intern ? string.Intern(id) : id, doc);
            }
        }
        return new XmlDocFile(members.ToImmutable(), path);
    }

    /// <summary>What <paramref name="member"/> contributes to a lookup, or <c>null</c> when it contributes nothing.</summary>
    private static XmlMemberDoc? Describe(XElement member, bool isType, bool intern)
    {
        var summary = member.Element("summary");
        var inheritDoc = member.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("inheritdoc", StringComparison.OrdinalIgnoreCase));
        var parameters = NoParams;
        if (isType)
            foreach (var param in member.Elements("param"))
                if (param.Attribute("name")?.Value is { } name && !parameters.ContainsKey(name))
                    parameters = parameters.Add(Text(name, intern), Text(Render(param), intern));
        if (summary is null && inheritDoc is null && parameters.IsEmpty)
            return null;
        return new XmlMemberDoc(
            summary is null ? null : Text(Render(summary), intern),
            inheritDoc is not null,
            inheritDoc?.Attribute("cref")?.Value is { } cref ? Text(cref, intern) : null,
            parameters);
    }

    private static string Text(string value, bool intern) => intern ? string.Intern(value) : value;

    /// <summary>
    /// Renders a documentation element to plain text, byte-compatible with the formatting-free
    /// rendering the platform has always shown: <c>&lt;see&gt;</c>/<c>&lt;seealso&gt;</c> become their
    /// text, else the <c>langword</c>, else the cref's last name segment (<c>T:Ns.Type</c> →
    /// <c>Type</c>, <c>M:Ns.T.M(System.Int32)</c> → <c>M</c>, <c>T:GlobalType</c> → <c>GlobalType</c>), else the <c>href</c>;
    /// <c>&lt;paramref&gt;</c> becomes its name; every other element
    /// (<c>&lt;c&gt;</c>, <c>&lt;para&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;list&gt;</c>, …) its own rendered
    /// content, trimmed. The common leading indentation of the source lines is removed.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The text.</returns>
    public static string Render(XElement element)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == "see" || child.Name == "seealso":
                    text.Append(SeeText(child));
                    break;
                case XElement child when child.Name == "paramref":
                    text.Append(child.Attribute("name")?.Value ?? child.Value);
                    break;
                case XElement child:
                    text.Append(Render(child).Trim());
                    break;
                case XText literal:
                    text.Append(literal.Value);
                    break;
                default:
                    text.Append(node);
                    break;
            }
        }
        return RemoveIndentation(text.ToString());
    }

    private static string SeeText(XElement see)
    {
        if (see.Value is { Length: > 0 } inner)
            return inner;
        if (see.Attribute("langword") is { } langword)
            return langword.Value;
        if (see.Attribute("cref") is { } cref)
        {
            var value = CrefTarget(cref.Value);
            var paren = value.IndexOf('(');
            if (paren >= 0)
                value = value[..paren];
            var dot = value.LastIndexOf('.');
            return dot < 0 ? value : value[(dot + 1)..];
        }
        return see.Attribute("href")?.Value ?? string.Empty;
    }

    /// <summary>
    /// The member a cref names, without its kind prefix: <c>T:Ns.Type</c> → <c>Ns.Type</c>,
    /// <c>T:GlobalType</c> → <c>GlobalType</c>, an unresolved <c>!:Name</c> → <c>Name</c>.
    /// </summary>
    private static string CrefTarget(string cref)
    {
        var value = cref.Trim();
        return value.Length >= 2 && value[1] == ':' && (char.IsAsciiLetterUpper(value[0]) || value[0] == '!')
            ? value[2..].Trim()
            : value;
    }

    /// <summary>
    /// Removes the first line's indentation from every line: strip <c>\r</c> and surrounding
    /// newlines, then replace each occurrence of "newline + the first line's leading blanks" with a
    /// bare newline.
    /// </summary>
    private static string RemoveIndentation(string documentation)
    {
        if (string.IsNullOrEmpty(documentation))
            return string.Empty;
        documentation = "\n" + documentation.Replace("\r", string.Empty).Trim('\n');
        var end = 1;
        while (end < documentation.Length && documentation[end] is ' ' or '\t')
            end++;
        var indentation = documentation[..end];
        if (indentation.Length > 1)
            documentation = documentation.Replace(indentation, "\n");
        return documentation.Trim('\n');
    }
}
