using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Domain;
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
            var xmlCommentsMethod = Type.Assembly.GetType($"{Type.Assembly.GetName().Name}.CodeComments")
                ?.GetMethod("GetSummary", BindingFlags.Public | BindingFlags.Static);

            var getFromXmlComment = xmlCommentsMethod == null ? null : (Func<string, string>)(x => xmlCommentsMethod.Invoke(null, new object[] { x })?.ToString());
            Description = GetDescription(Type, displayAttribute, getFromXmlComment);
            MemberDescriptions = Type.GetMembers()
                .Select(x => new KeyValuePair<string, string>(x.Name, GetFromXmlComments(x, getFromXmlComment)))
                .DistinctBy(x => x.Key)
                .ToDictionary();
            GroupName = displayAttribute?.GetGroupName();
            Order = displayAttribute?.GetOrder();
            var iconAttribute = Type.GetCustomAttribute<IconAttribute>();
            if (iconAttribute != null)
                Icon = new Icon(iconAttribute.Provider, iconAttribute.Id);

            Key = new (() => keyFunctionBuilder.GetKeyFunction(Type));
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

        public int? Order { get; }
        public string Description { get; }
        public string GroupName { get; }
        private Dictionary<string, string> MemberDescriptions { get; }

        public string GetDescription(string memberName) => MemberDescriptions.GetValueOrDefault(memberName, "Add description in the xml comments or in the display attribute");
        private string GetDescription(Type elementType, DisplayAttribute displayAttribute,
            Func<string, string> fromXmlComment)
        {
            return displayAttribute?.GetDescription()
                   ?? fromXmlComment?.Invoke($"T:{elementType.FullName}")
                   ?? "Add description in the xml comments or in the display attribute";
        }

        public virtual object GetKey(object instance) =>
            Key.Value.Function?.Invoke(instance)
            ?? throw new InvalidOperationException(
                "No key mapping is defined. Please specify in the configuration of the data sources source.");

        internal Lazy<KeyFunction> Key { get; init; }
    }
}
