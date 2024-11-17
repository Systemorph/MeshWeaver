using System.Xml.Serialization;

namespace MeshWeaver.Domain.Layout.Documentation.Model;

[XmlRoot("doc")]
public class Doc
{
    [XmlElement("assembly", typeof(Assembly))]
    public Assembly Assembly { get; init; }

    [XmlArray("members")]
    [XmlArrayItem("member")]
    public List<Member> Members { get; init; }
}
