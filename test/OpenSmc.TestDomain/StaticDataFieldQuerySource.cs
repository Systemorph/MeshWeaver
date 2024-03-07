using System.Reflection;
using OpenSmc.Data;

namespace OpenSmc.TestDomain
{
    public class StaticDataFieldQuerySource : IQuerySource
    {
        private IQueryable<T> Query<T>() where T : class
        {
            var dataProperty = typeof(T).GetField("Data", BindingFlags.Public | BindingFlags.Static);
            if (dataProperty == null)
                return null;
            return ((IEnumerable<T>)dataProperty.GetValue(null))?.AsQueryable();
        }

        public IReadOnlyCollection<T> GetData<T>() where T : class
        {
            return Query<T>()?.ToList().AsReadOnly();
        }
    }
}