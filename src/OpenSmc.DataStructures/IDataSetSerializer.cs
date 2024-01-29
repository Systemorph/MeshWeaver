namespace OpenSmc.DataStructures
{
    public interface IDataSetSerializer
    {
        string Serialize(IDataSet dataSet, bool indent = false);
        IDataSet Parse(TextReader reader);
    }
}