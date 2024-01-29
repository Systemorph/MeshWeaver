using OpenSmc.Arithmetics;
using OpenSmc.Arithmetics.MapOver;

namespace OpenSmc.DataCubes.Operations
{
    public class AggregateDataCube<T> : DataCubeBase<T>
    {
        private readonly ICollection<IDataCube<T>> cubes;

        public AggregateDataCube(ICollection<IDataCube<T>> cubes)
        {
            this.cubes = cubes;
        }

        public AggregateDataCube(IEnumerable<IDataCube<T>> enumerable)
        {
            cubes = enumerable as ICollection<IDataCube<T>> ?? enumerable.ToArray();
        }

        protected override IEnumerable<T> GetEnumerable()
        {
            return cubes.SelectMany(c => c);
        }

        public override IDataCube<T> Filter(params (string filter, object value)[] tuple)
        {
            return cubes.Select(c => c.Filter(tuple)).Aggregate();
        }

        public override IDataCube<T> Filter(Func<T, bool> filter)
        {
            return cubes.Select(c => c.Filter(filter)).Aggregate();;
        }

        public override IEnumerable<DataSlice<T>> GetSlices(params string[] dimensions)
        {
            return cubes.SelectMany(c => c.GetSlices(dimensions));
        }
    }

    public class MapOverDataCube<T> : DataCubeBase<T>
    {
        private readonly IDataCube<T> cube;
        private readonly ArithmeticOperation arithmeticOperation;
        private readonly double scalar;
        private readonly Func<double, T, T> method;

        public MapOverDataCube(IDataCube<T> cube, ArithmeticOperation arithmeticOperation, double scalar)
        {
            this.cube = cube;
            this.arithmeticOperation = arithmeticOperation;
            method = MapOverFields.GetMapOver<T>(arithmeticOperation);
            this.scalar = scalar;
        }

        protected override IEnumerable<T> GetEnumerable()
        {
            return cube.Select(e => method(scalar, e));
        }

        public override IDataCube<T> Filter(params (string filter, object value)[] tuple)
        {
            return new MapOverDataCube<T>(cube.Filter(tuple), arithmeticOperation, scalar);
        }

        public override IDataCube<T> Filter(Func<T, bool> filter)
        {
            return new MapOverDataCube<T>(cube.Filter(filter), arithmeticOperation, scalar);
        }

        public override IEnumerable<DataSlice<T>> GetSlices(params string[] dimensions)
        {
            return cube.GetSlices(dimensions).Select(s => new DataSlice<T>(method(scalar, s.Data), s.Tuple));
        }
    }
}