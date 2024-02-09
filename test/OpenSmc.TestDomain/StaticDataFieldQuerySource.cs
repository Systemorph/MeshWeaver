using System.Reflection;
using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.TestDomain
{
    public class StaticDataFieldQuerySource : IQuerySource
    {
        public IQueryable<T> Query<T>() where T : class
        {
            var dataProperty = typeof(T).GetField("Data", BindingFlags.Public | BindingFlags.Static);
            if (dataProperty == null)
                return null;
            return ((IEnumerable<T>)dataProperty.GetValue(null))?.AsQueryable();
        }
    }
}