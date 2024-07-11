using System.Xml.Serialization;

namespace OpenSmc.Documentation.Model;

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
