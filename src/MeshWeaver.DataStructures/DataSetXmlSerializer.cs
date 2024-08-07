using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace MeshWeaver.DataStructures
{
    public sealed class DataSetXmlSerializer : IDataSetSerializer
    {
        public static readonly IDataSetSerializer Instance = new DataSetXmlSerializer();

        private DataSetXmlSerializer()
        {
        }

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

        public IDataSet Parse(TextReader reader)
        {
            using (var xmlTextReader = new XmlTextReader(reader))
            {
                // Next line is workaround for Veracode: Improper Restriction of XML External Entity Reference (CWE ID 611)
                xmlTextReader.XmlResolver = null;

                var serializer = new XmlSerializer(typeof(DataSet));
                return (IDataSet)serializer.Deserialize(xmlTextReader);
            }
        }
    }
}