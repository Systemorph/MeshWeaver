using System.Xml.Serialization;

namespace OpenSmc.Documentation.Model;

public class Assembly
{
    [XmlElement("name")]
    public string Name { get; init; }
}
