namespace MeshWeaver.DataCubes
{
    public interface IFilterable
    {
        public object Filter(params (string filter, object value)[] filter);
    }

    public interface IFilterable<out T> : IFilterable
    {
        new T Filter(params (string filter, object value)[] filter);
    }
}