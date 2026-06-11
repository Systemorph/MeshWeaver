using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace MeshWeaver.Blazor;

/// <summary>
/// Generates POCO types at runtime with specified properties.
/// Useful for creating strongly-typed objects from dynamic configurations.
///
/// Instance service — register as a singleton (see RadzenServiceExtensions); the
/// memoization cache is an instance field so it dies with the host's ServiceProvider
/// (NoStaticState.md — no process-wide static cache, no Clear() test hook). Generated
/// assemblies are collectible (<see cref="AssemblyBuilderAccess.RunAndCollect"/>) so
/// cached types can unload once the cache and all instances are gone.
/// </summary>
public sealed class DynamicTypeGenerator
{
    private readonly Dictionary<string, Type> typeCache = new();
    private readonly object cacheLock = new();

    /// <summary>
    /// Generates or retrieves a cached type with the specified properties.
    /// </summary>
    /// <param name="properties">List of property names and their CLR type names</param>
    /// <param name="typeName">Optional custom type name (auto-generated if not provided)</param>
    /// <returns>A Type with the specified properties</returns>
    public Type GenerateType(IEnumerable<(string Name, string TypeName)> properties, string? typeName = null)
    {
        var propList = properties.ToList();

        // Create a cache key based on the properties
        var cacheKey = string.Join(";", propList.Select(p => $"{p.Name}:{p.TypeName}"));

        lock (cacheLock)
        {
            if (typeCache.TryGetValue(cacheKey, out var cachedType))
                return cachedType;

            var generatedType = GenerateTypeInternal(propList, typeName);
            typeCache[cacheKey] = generatedType;
            return generatedType;
        }
    }

    private static Type GenerateTypeInternal(List<(string Name, string TypeName)> properties, string? typeName)
    {
        var assemblyName = new AssemblyName("DynamicTypes");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName, AssemblyBuilderAccess.RunAndCollect);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

        var actualTypeName = typeName ?? $"DynamicType_{Guid.NewGuid():N}";
        var typeBuilder = moduleBuilder.DefineType(
            actualTypeName,
            TypeAttributes.Public);

        foreach (var (propertyName, propertyTypeName) in properties)
        {
            AddProperty(typeBuilder, propertyName, propertyTypeName);
        }

        return typeBuilder.CreateType()!;
    }

    private static void AddProperty(TypeBuilder typeBuilder, string propertyName, string propertyTypeName)
    {
        var propertyType = Type.GetType(propertyTypeName) ?? typeof(object);

        // Define backing field
        var fieldBuilder = typeBuilder.DefineField(
            $"_{propertyName}",
            propertyType,
            FieldAttributes.Private);

        // Define property
        var propertyBuilder = typeBuilder.DefineProperty(
            propertyName,
            PropertyAttributes.HasDefault,
            propertyType,
            null);

        // Define getter
        var getterBuilder = typeBuilder.DefineMethod(
            $"get_{propertyName}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            propertyType,
            Type.EmptyTypes);

        var getterIL = getterBuilder.GetILGenerator();
        getterIL.Emit(OpCodes.Ldarg_0);
        getterIL.Emit(OpCodes.Ldfld, fieldBuilder);
        getterIL.Emit(OpCodes.Ret);

        // Define setter
        var setterBuilder = typeBuilder.DefineMethod(
            $"set_{propertyName}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            null,
            new[] { propertyType });

        var setterIL = setterBuilder.GetILGenerator();
        setterIL.Emit(OpCodes.Ldarg_0);
        setterIL.Emit(OpCodes.Ldarg_1);
        setterIL.Emit(OpCodes.Stfld, fieldBuilder);
        setterIL.Emit(OpCodes.Ret);

        propertyBuilder.SetGetMethod(getterBuilder);
        propertyBuilder.SetSetMethod(setterBuilder);
    }
}
