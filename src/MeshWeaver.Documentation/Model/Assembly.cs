using System.Xml.Serialization;

namespace MeshWeaver.Documentation.Model;

public class Assembly
{
    [XmlElement("name")]
    public string Name { get; init; }
}
