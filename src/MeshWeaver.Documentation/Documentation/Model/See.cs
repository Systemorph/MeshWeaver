using System.Xml.Serialization;

namespace MeshWeaver.Domain.Layout.Documentation.Model;

public record See
{
    [XmlAttribute("cref")]
    public string Cref { get; init; }
}
