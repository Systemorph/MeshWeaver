using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Pivot.Processors
{
    public static class PivotExtensions
    {
        public static IEnumerable<(
            string SystemName,
            string DisplayName,
            PropertyInfo Property
        )> GetPropertiesForProcessing(this Type tObject, string prefix)
        {
            var dot = string.IsNullOrEmpty(prefix) ? string.Empty : ".";
            var properties = tObject
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                // TODO: This has to be properly linked to access control (2021/05/06, Roland Buergi)
                .Where(x => !x.HasAttribute<NotVisibleAttribute>())
                .OrderBy(x => x.GetCustomAttribute<DisplayAttribute>()?.GetOrder() ?? int.MaxValue)
                .Select(p =>
                    (
                        systemName: prefix == null ? p.Name : $"{prefix}{dot}{p.Name}",
                        displayName: p.GetCustomAttribute<DisplayAttribute>()?.Name
                            ?? p.Name.Wordify(),
                        property: p
                    )
                );
            return properties;
        }

        public static T[] NullIfEmpty<T>(this T[] array)
        {
            if (array?.Length > 0)
                return array;
            return null;
        }
    }
}
