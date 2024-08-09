namespace System.Text.Json.Serialization.DataContractExtensions;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

// this is temporary workaround to respect EnumMember attribute for customizing enum value serialization
// .net 9 ships with JsonStringEnumMemberName attribute which is respected by build-in JsonStringEnumConverter
// https://github.com/dotnet/runtime/issues/74385 for details

/// <summary>
/// Provides an AOT-compatible extension for <see cref="JsonStringEnumConverter"/> that adds support for <see cref="EnumMemberAttribute"/>.
/// </summary>
/// <typeparam name="TEnum">The type of the <see cref="TEnum"/>.</typeparam>
public sealed class EnumMemberJsonStringEnumConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>
    : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
{
    public EnumMemberJsonStringEnumConverter() : base(namingPolicy: ResolveNamingPolicy())
    {
    }

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
            => map.TryGetValue(name, out string? newName) ? newName : name;
    }
}

/// <summary>
/// Provides a non-generic variant of <see cref="EnumMemberJsonStringEnumConverter"/> that is not compatible with Native AOT.
/// </summary>
/// <typeparam name="TEnum">The type of the <see cref="TEnum"/>.</typeparam>
[RequiresUnreferencedCode("EnumMemberAttribute annotations might get trimmed.")]
[RequiresDynamicCode("Requires dynamic code generation.")]
public sealed class EnumMemberJsonStringEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type typedFactory = typeof(EnumMemberJsonStringEnumConverter<>).MakeGenericType(typeToConvert);
        var innerFactory = (JsonConverterFactory)Activator.CreateInstance(typedFactory)!;
        return innerFactory.CreateConverter(typeToConvert, options);
    }
}
