using OpenSmc.DataStructures;

namespace OpenSmc.Import
{
    public record TableMapping(string TableName, Func<IDataSet, IDataTable, IEnumerable<object>> MappingFunction)
    {
        public virtual IEnumerable<object> Map(IDataSet dataSet, IDataTable dataSetTable)
            => MappingFunction.Invoke(dataSet, dataSetTable);
    }

}
