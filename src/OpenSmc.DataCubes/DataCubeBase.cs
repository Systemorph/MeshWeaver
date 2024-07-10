using System.Collections;

namespace OpenSmc.DataCubes
{
    public abstract class DataCubeBase<T> : IDataCube<T>
    {
        public IEnumerator<T> GetEnumerator() => GetEnumerable().GetEnumerator();

        protected abstract IEnumerable<T> GetEnumerable();

        public abstract IDataCube<T> Filter(params (string filter, object value)[] tuple);
        
        public abstract IDataCube<T> Filter(Func<T, bool> filter);

        public virtual IEnumerable<DimensionDescriptor> GetDimensionDescriptors()
        {
            return TuplesUtils<T>.GetDimensionDescriptors();
        }

        public virtual IEnumerable<DimensionDescriptor> GetDimensionDescriptors(bool isByRow, params string[] dimensions)
        {
            return TuplesUtils<T>.GetDimensionDescriptors(dimensions);
        }

        public abstract IEnumerable<DataSlice<T>> GetSlices(params string[] dimensions);


        #region NonGenericInterfaces

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerable<IDataSlice> IDataCube.GetSlices(params string[] dimensions)
        {
            return GetSlices(dimensions).Cast<IDataSlice>();
        }

        IDataCube IDataCube.Filter(params (string filter, object value)[] tuple)
        {
            return Filter(tuple);
        }

        object IFilterable.Filter(params (string filter, object value)[] filter)
        {
            return Filter(filter);
        }

        #endregion
    }
}
