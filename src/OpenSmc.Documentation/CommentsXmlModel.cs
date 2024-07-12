using System.Collections.Immutable;
using System.Xml.Serialization;

namespace OpenSmc.Documentation;

[XmlRoot("doc")]
public class Doc
{
    public Assembly Assembly { get; init; }

    [XmlArray("members")]
    [XmlArrayItem("member")]
    public List<Member> Members { get; init; }
}

public class Assembly
{
    [XmlAttribute("name")]
    public string Name { get; init; }
}

public class Member
{
    [XmlAttribute("name")]
    public string Name { get; init; }

    // Adjusted to support mixed content including 'See' elements
    [XmlElement("summary", typeof(Summary))]
    public Summary Summary { get; init; }

    [XmlElement("param")]
    public List<Param> Params { get; init; }
}

public record Param
{
    [XmlAttribute("name")]
    public string Name { get; init; }

    [XmlText]
    public string Description { get; init; }
}

public record Summary
{
    [XmlText]
    public string Text { get; init; }

    [XmlElement("see")]
    public See See { get; init; }
}

public record See
{
    [XmlAttribute("cref")]
    public string Cref { get; init; }
}
