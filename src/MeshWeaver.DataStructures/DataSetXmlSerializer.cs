using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace MeshWeaver.DataStructures;

/// <summary>
/// An <c>IDataSetSerializer</c> that serializes and parses <c>IDataSet</c> instances as XML.
/// Use the shared <c>Instance</c> singleton.
/// </summary>
public sealed class DataSetXmlSerializer : IDataSetSerializer
{
    /// <summary>The shared singleton instance of the XML data-set serializer.</summary>
    public static readonly IDataSetSerializer Instance = new DataSetXmlSerializer();

    private DataSetXmlSerializer()
    {
    }

    /// <summary>Serializes the data set to an XML string.</summary>
    /// <param name="dataSet">The data set to serialize.</param>
    /// <param name="indent">When <c>true</c>, the XML is indented for readability.</param>
    /// <returns>The XML representation of the data set.</returns>
    public string Serialize(IDataSet dataSet, bool indent)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = indent
        };
        using (var xmlWriter = XmlWriter.Create(sb, settings))
        {
            var serializer = new XmlSerializer(typeof(DataSet));
            serializer.Serialize(xmlWriter, dataSet);
        }
        return sb.ToString();
    }

    /// <summary>Parses a data set from XML read from the given reader. External entity resolution is disabled.</summary>
    /// <param name="reader">Reader positioned at the serialized XML.</param>
    /// <returns>The deserialized data set.</returns>
    public IDataSet Parse(TextReader reader)
    {
        using (var xmlTextReader = new XmlTextReader(reader))
        {
            // Next line is workaround for Veracode: Improper Restriction of XML External Entity Reference (CWE ID 611)
            xmlTextReader.XmlResolver = null;

            var serializer = new XmlSerializer(typeof(DataSet));
            return (IDataSet)serializer.Deserialize(xmlTextReader)!;
        }
    }
}
