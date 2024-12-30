using System.Xml.Serialization;

namespace MeshWeaver.Data.Documentation.Model;

public class Assembly
{
    [XmlElement("name")]
    public string Name { get; init; }
}
