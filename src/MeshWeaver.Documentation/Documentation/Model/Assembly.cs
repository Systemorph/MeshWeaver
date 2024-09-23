using System.Xml.Serialization;

namespace MeshWeaver.Domain.Layout.Documentation.Model;

public class Assembly
{
    [XmlElement("name")]
    public string Name { get; init; }
}
