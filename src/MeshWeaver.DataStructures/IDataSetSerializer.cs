namespace MeshWeaver.DataStructures
{
    /// <summary>Serializes an <c>IDataSet</c> to text and parses it back.</summary>
    public interface IDataSetSerializer
    {
        /// <summary>Serializes the data set to its text representation.</summary>
        /// <param name="dataSet">The data set to serialize.</param>
        /// <param name="indent">When <c>true</c>, the output is indented for readability.</param>
        /// <returns>The serialized data set as a string.</returns>
        string Serialize(IDataSet dataSet, bool indent = false);
        /// <summary>Parses a data set from the given text reader.</summary>
        /// <param name="reader">Reader positioned at the serialized data set.</param>
        /// <returns>The deserialized data set.</returns>
        IDataSet Parse(TextReader reader);
    }
}