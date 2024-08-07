using System.Reflection;
using AspectCore.Extensions.Reflection;

namespace MeshWeaver.Scopes.DataCubes
{
    internal class DimensionProperty
    {
        public DimensionProperty(string dimensionSystemName, Type dimensionType, PropertyInfo property)
        {
            DimensionSystemName = dimensionSystemName;
            DimensionType = dimensionType;
            Property = property;
            Reflector = property.GetReflector();
        }

        public PropertyInfo Property { get; }
        public Type DimensionType { get; }
        public string DimensionSystemName { get; }

        public PropertyReflector Reflector { get; }
    }
}