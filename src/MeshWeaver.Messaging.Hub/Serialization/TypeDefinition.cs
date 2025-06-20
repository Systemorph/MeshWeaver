using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Messaging.Serialization
{
    public record TypeDefinition : ITypeDefinition
    {
        public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder)
        {
            Type = elementType;
            CollectionName = typeName;

            var displayAttribute = Type.GetCustomAttribute<DisplayAttribute>();
            DisplayName = displayAttribute?.GetName() ?? Type.Name.Wordify();

            GroupName = displayAttribute?.GetGroupName();
            Order = displayAttribute?.GetOrder();
            var iconAttribute = Type.GetCustomAttribute<IconAttribute>();
            if (iconAttribute != null)
                Icon = new Icon(iconAttribute.Provider, iconAttribute.Id);

            Key = new(() => keyFunctionBuilder.GetKeyFunction(Type));
        }

        public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder, Address address)
            : this(elementType, typeName, keyFunctionBuilder)
        {
            Address = address;
        }

        private string GetFromXmlComments(MemberInfo member, Func<string, string> getMemberComment)
        {
            if (getMemberComment == null)
                return null;
            return member switch
            {
                Type type => getMemberComment($"T:{type.FullName}"),
                PropertyInfo => getMemberComment($"P:{member.ReflectedType?.FullName}.{member.Name}"),
                MethodInfo => getMemberComment($"M:{member.ReflectedType?.FullName}.{member.Name}"),
                FieldInfo => getMemberComment($"F:{member.ReflectedType?.FullName}.{member.Name}"),
                _ => null
            };
        }

        public Type Type { get; init; }
        public string DisplayName { get; init; }
        public string CollectionName { get; init; }
        public object Icon { get; init; }
        public Address Address { get; init; }

        public int? Order { get; }
        public string GroupName { get; }


        public virtual object GetKey(object instance) =>
            Key.Value.Function?.Invoke(instance)
            ?? throw new InvalidOperationException(
                $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");

        public Type GetKeyType() =>
            Key.Value?.KeyType
            ?? throw new InvalidOperationException(
                $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");
        internal Lazy<KeyFunction> Key { get; init; }
    }
}
