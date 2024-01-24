using System.Collections;

namespace OpenSmc.DataCubes
{
    public interface IDataCube : IEnumerable, IFilterable
    {
        IEnumerable<IDataSlice> GetSlices(params string[] dimensions);
        
        new IDataCube Filter(params (string filter, object value)[] tuple);

        IEnumerable<DimensionDescriptor> GetDimensionDescriptors(bool isByRow, params string[] dimensions);
    }

    public interface IDataCube<T> : IDataCube, IEnumerable<T>
    {
        new IEnumerable<DataSlice<T>> GetSlices(params string[] dimensions);

        new IDataCube<T> Filter(params (string filter, object value)[] tuple);
        
        IDataCube<T> Filter(Func<T, bool> filter);

        IDataCube<T> Filter(DimensionTuple tuple) => Filter(tuple.AsEnumerable().ToArray());

        public static IDataCube<T> operator +(IDataCube<T> a, IDataCube<T> b) => Sum(a, b);
        public static IDataCube<T> operator +(IDataCube<T> a, double b) => Sum(a, b);
        public static IDataCube<T> operator +(double a, IDataCube<T> b) => Sum(a, b);
        public static IDataCube<T> operator -(IDataCube<T> a, IDataCube<T> b) => Subtract(a, b);
        public static IDataCube<T> operator -(IDataCube<T> a, double b) => Subtract(a, b);
        public static IDataCube<T> operator *(double a, IDataCube<T> b) => Multiply(a, b);
        public static IDataCube<T> operator *(IDataCube<T> a, double b) => Multiply(a, b);
        public static IDataCube<T> operator /(IDataCube<T> a, double b) => Divide(a, b);
        public static IDataCube<T> operator ^(IDataCube<T> a, double b) => Power(a, b);
    }
}