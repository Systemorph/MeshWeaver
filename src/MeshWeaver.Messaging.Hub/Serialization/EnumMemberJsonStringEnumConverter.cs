using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

// this is temporary workaround to respect EnumMember attribute for customizing enum value serialization
// .net 9 ships with JsonStringEnumMemberName attribute which is respected by build-in JsonStringEnumConverter
// https://github.com/dotnet/runtime/issues/74385 for details

/// <summary>
/// Provides an AOT-compatible extension for <see cref="JsonStringEnumConverter"/> that adds support for <see cref="EnumMemberAttribute"/>.
/// </summary>
/// <typeparam name="TEnum">The enum type whose <see cref="EnumMemberAttribute"/> values are honored.</typeparam>
public sealed class EnumMemberJsonStringEnumConverter<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>()
    : JsonStringEnumConverter<TEnum>(namingPolicy: ResolveNamingPolicy())
    where TEnum : struct, Enum
{
    private static JsonNamingPolicy? ResolveNamingPolicy()
    {
        var map = typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (f.Name, AttributeName: f.GetCustomAttribute<EnumMemberAttribute>()?.Value))
            .Where(pair => pair.AttributeName != null)
            .ToDictionary();

        return map.Count > 0 ? new EnumMemberNamingPolicy(map!) : null;
    }

    private sealed class EnumMemberNamingPolicy(Dictionary<string, string> map) : JsonNamingPolicy
    {
        public override string ConvertName(string name)
            => map.TryGetValue(name, out var newName) ? newName : name;
    }
}

/// <summary>
/// Provides a non-generic variant of <see cref="EnumMemberJsonStringEnumConverter"/> that is not compatible with Native AOT.
/// </summary>
[RequiresUnreferencedCode("EnumMemberAttribute annotations might get trimmed.")]
[RequiresDynamicCode("Requires dynamic code generation.")]
public sealed class EnumMemberJsonStringEnumConverter : JsonConverterFactory
{
    /// <summary>
    /// Indicates that this factory produces converters for any enum type.
    /// </summary>
    /// <param name="typeToConvert">The candidate type.</param>
    /// <returns><c>true</c> when <paramref name="typeToConvert"/> is an enum; otherwise <c>false</c>.</returns>
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    /// <summary>
    /// Builds the strongly-typed <see cref="EnumMemberJsonStringEnumConverter{TEnum}"/> for the
    /// requested enum type so that its <see cref="EnumMemberAttribute"/> values are honored.
    /// </summary>
    /// <param name="typeToConvert">The enum type to create a converter for.</param>
    /// <param name="options">The active serializer options.</param>
    /// <returns>A converter instance specialized for <paramref name="typeToConvert"/>.</returns>
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var typedFactory = typeof(EnumMemberJsonStringEnumConverter<>).MakeGenericType(typeToConvert);
        var innerFactory = (JsonConverterFactory)Activator.CreateInstance(typedFactory)!;
        return innerFactory.CreateConverter(typeToConvert, options);
    }
}
