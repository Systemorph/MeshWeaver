using System.Xml.Serialization;

namespace MeshWeaver.Domain.Layout.Documentation.Model;

public record Summary
{
    [XmlText]
    public string Text { get; init; }

    [XmlElement("see")]
    public See See { get; init; }
}
