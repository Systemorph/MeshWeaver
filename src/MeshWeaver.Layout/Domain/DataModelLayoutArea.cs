using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Reflection;

namespace MeshWeaver.Layout.Domain;

/// <summary>
/// Layout area that renders a Mermaid class diagram of the data domain and per-type property/derived-type detail pages.
/// Exposed as the <c>DataModel</c> area on domain hubs; the optional Id parameter selects a specific type's detail page.
/// </summary>
[Display(GroupName = "Data Model")]
public static class DataModelLayoutArea
{
    /// <summary>
    /// Provides a diagram with the data model of the data domain.
    /// </summary>
    /// <param name="host"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    [Browsable(false)]
    public static UiControl DataModel(LayoutAreaHost host, RenderingContext context)
    {
        var _ = context; // currently unused
        var argId = host.Reference.Id?.ToString();
        if (!string.IsNullOrWhiteSpace(argId))
        {
            var typeName = argId;
            return host.RenderTypeDetails(typeName);
        }

        return new MarkdownControl(host.GetMermaidDiagram());
    }

    private static IOrderedEnumerable<ITypeDefinition> GetTypes(this LayoutAreaHost host)
    {
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        return host
            .Workspace
            .DataContext
            .TypeSources
            .Values
            .Select(x => x.TypeDefinition)
            .SelectMany(x => typeRegistry.Types.Select(t => t.Value).Where(t => !t.Equals(x) && x.Type.IsAssignableFrom(t.Type)))
            .DistinctBy(x => x.Type)
            .OrderBy(x => x.Order ?? int.MaxValue)
            .ThenBy(x => x.GroupName);
    }

    private static IEnumerable<ITypeDefinition> GetAllDomainTypes(this LayoutAreaHost host)
    {
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        return typeRegistry.Types
            .Select(t => t.Value)
            .Where(td => IsEligibleDomainType(td.Type))
            .DistinctBy(x => x.Type);
    }

    /// <summary>
    /// True when <paramref name="t"/> is a class worth showing in a data-model
    /// diagram — excludes primitives, enums, and common framework value types.
    /// </summary>
    public static bool IsEligibleDomainType(Type t)
    {
        // Exclude primitives, enums, and common framework value types
        if (t.IsPrimitive || t.IsEnum) return false;
        if (t == typeof(string) || t == typeof(DateTime) || t == typeof(decimal) || t == typeof(Guid)) return false;
        // Only include classes (records are classes too)
        return t.IsClass;
    }

    private static string GetMermaidDiagram(this LayoutAreaHost host)
        => BuildMermaidDiagram(
            host.GetTypes().ToArray(),
            host.GetAllDomainTypes().ToArray(),
            $"/{host.Hub.Address}/DataModel");

    /// <summary>
    /// Builds the Mermaid class-diagram markdown for an explicit set of types —
    /// the reusable core behind <see cref="DataModel"/>, also used by the NodeType
    /// page to render the data model of a type's compiled instance configuration.
    /// </summary>
    /// <param name="seedTypes">The types to seed each diagram group with; the closure
    /// over property/derived types from <paramref name="allDomainTypes"/> is added.</param>
    /// <param name="allDomainTypes">The full domain-type lookup used to resolve
    /// property targets, derived types, and click links.</param>
    /// <param name="linkBase">Href prefix for clickable class nodes, e.g.
    /// <c>/{address}/DataModel</c> or <c>/{nodeTypePath}/$Model</c>.</param>
    /// <param name="includeGroupHeadings">Whether to emit a <c>## {group}</c> heading
    /// above each diagram group.</param>
    public static string BuildMermaidDiagram(
        IReadOnlyCollection<ITypeDefinition> seedTypes,
        IReadOnlyCollection<ITypeDefinition> allDomainTypes,
        string linkBase,
        bool includeGroupHeadings = true)
    {
        var allDomain = allDomainTypes.ToArray();
        var allByType = allDomain.ToDictionary(d => d.Type, d => d);
        var sb = new StringBuilder();

        // Group types into subgraphs based on some criteria, e.g., namespace or group name
        var groupedTypes = seedTypes.GroupBy(t => t.GroupName ?? "Default");

        foreach (var group in groupedTypes)
        {
            // Expand group by including property domain types and their derived types
            var groupSet = new HashSet<ITypeDefinition>(group);
            var grew = true;
            while (grew)
            {
                grew = false;
                foreach (var td in groupSet.ToArray())
                {
                    foreach (var p in td.Type.GetProperties())
                    {
                        var pt = GetDomainTargetTypeForProperty(p, allByType);
                        if (pt != null && allByType.TryGetValue(pt, out var propDef))
                        {
                            if (groupSet.Add(propDef)) grew = true;
                            // Add derived types of the property type as well
                            foreach (var d in allDomain.Where(x => x.Type.BaseType == pt))
                            {
                                if (groupSet.Add(d)) grew = true;
                            }
                        }
                    }
                }
            }

            if (includeGroupHeadings)
                sb.AppendLine($"## {group.Key}");
            sb.AppendLine("```mermaid");
            sb.AppendLine("classDiagram");

            // Define classes
            foreach (var type in groupSet)
            {
                var typeName = type.Type.Name;
                var link = $"{linkBase}/{typeName}";

                var propLines = type.Type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(p => $"  {GetPropertyTypeDisplayName(p)} {p.Name}")
                    .ToList();

                var methodLines = type.Type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName && !ExcludedMethods.Contains(m.Name))
                    .Select(m => $"  {m.ReturnType.Name} {m.Name}({GetParameters(m)})")
                    .ToList();

                if (propLines.Count == 0 && methodLines.Count == 0)
                {
                    sb.AppendLine($"class {typeName}");
                }
                else
                {
                    sb.AppendLine($"class {typeName} {{");
                    foreach (var l in propLines) sb.AppendLine(l);
                    foreach (var l in methodLines) sb.AppendLine(l);
                    sb.AppendLine("}");
                }

                // Clickable node linking to detailed view in same page (use _self target)
                sb.AppendLine($"click {typeName} href \"{link}\" \"View {typeName} details\" _self");
            }

            // Add inheritance relationships
            foreach (var type in groupSet)
            {
                var typeName = type.Type.Name;

                // Check for base class inheritance (within the same group)
                if (type.Type.BaseType != null && type.Type.BaseType != typeof(object))
                {
                    var baseTypeName = type.Type.BaseType.Name;
                    var baseTypeInGroup = groupSet.Any(t => t.Type.Name == baseTypeName);
                    if (baseTypeInGroup)
                    {
                        sb.AppendLine($"{baseTypeName} <|-- {typeName}");
                    }
                }
            }

            // Add composition/aggregation relationships for domain (registry) types
            foreach (var type in groupSet)
            {
                var typeName = type.Type.Name;

                // Consider only properties declared on this type to avoid duplicating inherited relationships
                foreach (var prop in type.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var propType = prop.PropertyType;
                    // Resolve to domain target type: unwrap Nullable<T> and single-arg generics (e.g., List<T>)
                    var target = GetDomainTargetTypeForProperty(prop, allByType);
                    if (target != null && allByType.ContainsKey(target) && groupSet.Any(t => t.Type == target) && target != type.Type)
                    {
                        var targetName = target.Name;
                        // Use composition for non-nullable reference types; aggregation otherwise
                        var relationship = propType.IsClass && Nullable.GetUnderlyingType(propType) == null
                            ? "*--"
                            : "o--";
                        sb.AppendLine($"{typeName} {relationship} {targetName} : {prop.Name}");
                    }
                }
            }

            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static UiControl RenderTypeDetails(this LayoutAreaHost host, ITypeDefinition typeDef)
        => RenderTypeDetails(
            typeDef,
            host.GetAllDomainTypes().ToArray(),
            $"/{host.Hub.Address}/DataModel",
            td => string.IsNullOrWhiteSpace(td.CollectionName)
                ? null
                : $"/{host.Hub.Address}/Catalog/{td.CollectionName}");

    /// <summary>
    /// Renders the property/derived-type detail page for one type — the reusable
    /// core behind the Id-parameterised <see cref="DataModel"/>, also used by the
    /// NodeType page's instance-model view.
    /// </summary>
    /// <param name="typeDef">The type to detail.</param>
    /// <param name="allDomainTypes">The domain-type lookup for property links and derived types.</param>
    /// <param name="linkBase">Href prefix for type links (same semantics as
    /// <see cref="BuildMermaidDiagram"/>).</param>
    /// <param name="catalogHref">Optional per-type catalog link factory; null suppresses
    /// the catalog navigation icon.</param>
    public static UiControl RenderTypeDetails(
        ITypeDefinition typeDef,
        IReadOnlyCollection<ITypeDefinition> allDomainTypes,
        string linkBase,
        Func<ITypeDefinition, string?>? catalogHref = null)
    {
        var type = typeDef.Type;
        var allByType = allDomainTypes
            .DistinctBy(td => td.Type)
            .ToDictionary(td => td.Type, td => td);
        var sb = new StringBuilder();

        var typeSummary = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(type);

        // Title with right-aligned navigation icons
        var navigationIcons = $"<a href=\"{linkBase}\" title=\"Data Model Overview\" style=\"text-decoration: none; font-size: 2em; line-height: 1;\">⧉</a>";
        if (type.BaseType is { } bt && bt != typeof(object) && allByType.ContainsKey(bt))
            navigationIcons += $" <a href=\"{linkBase}/{bt.Name}\" title=\"Base: {bt.Name}\" style=\"text-decoration: none; font-size: 2em; line-height: 1;\">🔝</a>";
        if (catalogHref?.Invoke(typeDef) is { } catalogLink)
            navigationIcons += $" <a href=\"{catalogLink}\" title=\"View Catalog\" style=\"text-decoration: none; font-size: 2em; line-height: 1;\">🗃️</a>";

        var titleWithNav = $"<div style=\"display: flex !important; justify-content: space-between !important; align-items: center !important; margin-bottom: 1rem; width: 100%;\"><h1 style=\"margin: 0; flex-grow: 1;\">{type.Name}</h1><div style=\"flex-shrink: 0;\">{navigationIcons}</div></div>";
        sb.AppendLine(titleWithNav);
        if (!string.IsNullOrWhiteSpace(typeSummary))
            sb.AppendLine($"{typeSummary}");
        sb.AppendLine();

        // Properties section as a single table: inherited first, then declared
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new
            {
                Prop = p,
                IsInherited = p.DeclaringType != type,
                Declaring = p.DeclaringType?.Name ?? "Base",
                TypeName = GetPropertyTypeDisplayName(p),
                Summary = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(p)
            })
            .OrderByDescending(x => x.IsInherited) // inherited (from base) first; keep original declared order
            .ToList();

        sb.AppendLine("## Properties");
        if (!props.Any())
        {
            sb.AppendLine("*None*");
        }
        else
        {
            // Legend: ↑ inherited from base, • declared on this type
            sb.AppendLine("|  | Name | Type | From | Summary |");
            sb.AppendLine("|---|------|------|------|---------|");
            foreach (var x in props)
            {
                var origin = x.IsInherited ? "↑" : "•";
                var name = x.Prop.Name;
                var typeMd = GetPropertyTypeCell(allByType, linkBase, x.Prop, x.TypeName);
                var from = x.IsInherited ? x.Declaring : "this";
                var summary = string.IsNullOrWhiteSpace(x.Summary) ? "" : x.Summary.Replace("\n", " ").Replace("|", "\\|");
                sb.AppendLine($"| {origin} | **{name}** | {typeMd} | {from} | {summary} |");
            }
            sb.AppendLine();
        }

        // Derived types navigation last
        var derived = allDomainTypes.Where(t => t.Type.BaseType == type).Select(t => t.Type).OrderBy(t => t.Name).ToArray();
        if (derived.Any())
        {
            sb.AppendLine("## Derived Types");
            foreach (var dt in derived)
            {
                sb.AppendLine($"- [{dt.Name}]({linkBase}/{dt.Name})");
            }
            sb.AppendLine();
        }

        return new MarkdownControl(sb.ToString());
    }

    private static UiControl RenderTypeDetails(this LayoutAreaHost host, string typeName)
    {
        var typeRegistry = host.Hub.TypeRegistry;
        var typeDef = typeRegistry.GetTypeDefinition(typeName);
        if (typeDef == null)
        {
            return Controls.Markdown($"# Type not found\n\nType '{typeName}' was not found in the domain.");
        }
        return host.RenderTypeDetails(typeDef);
    }

    private static string GetParameters(MethodInfo method)
    {
        return string.Join(", ",
            method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")
        );
    }

    private static string GetSimpleTypeName(Type type)
    {
        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(type);
        if (underlyingType != null)
        {
            return underlyingType.Name;
        }

        // Handle generic types
        if (type.IsGenericType)
        {
            var genericTypeName = type.Name.Split('`')[0];
            var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetSimpleTypeName));
            return $"{genericTypeName}[{genericArgs}]";
        }

        // Handle common .NET types that might cause issues
        if (type == typeof(string)) return "String";
        if (type == typeof(int)) return "Int32";
        if (type == typeof(bool)) return "Boolean";
        if (type == typeof(double)) return "Double";
        if (type == typeof(DateTime)) return "DateTime";

        return type.Name;
    }

    private static string GetPropertyTypeDisplayName(PropertyInfo property)
    {
        var type = property.PropertyType;

        var underlying = Nullable.GetUnderlyingType(type);
        var baseName = GetSimpleTypeName(underlying ?? type);

        var isNullable = underlying != null;
        if (!isNullable && !type.IsValueType)
        {
            // Inspect reference-type nullability annotations
            var nic = new NullabilityInfoContext();
            var info = nic.Create(property);
            isNullable = info.WriteState == NullabilityState.Nullable || info.ReadState == NullabilityState.Nullable;
        }

        return isNullable ? baseName + "?" : baseName;
    }

    private static string GetPropertyTypeCell(
        IDictionary<Type, ITypeDefinition> allByType,
        string linkBase,
        PropertyInfo property,
        string displayName)
    {
        // Determine underlying type for nullables
        var t = GetDomainTargetTypeForProperty(property, allByType);

        if (t != null && allByType.ContainsKey(t))
        {
            var linked = $"[`{displayName}`]({linkBase}/{t.Name})";
            return linked;
        }

        return $"`{displayName}`";
    }

    private static Type? GetDomainTargetTypeForProperty(PropertyInfo property, IDictionary<Type, ITypeDefinition> registry)
    {
        var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (t.IsArray)
        {
            t = t.GetElementType()!;
        }
        else if (t.IsGenericType)
        {
            var args = t.GetGenericArguments();
            // Only consider single-generic-arg collections; skip dictionaries and multi-arg types
            if (args.Length == 1)
            {
                t = args[0];
            }
            else
            {
                return null;
            }
        }

        return registry.ContainsKey(t) && IsEligibleDomainType(t) ? t : null;
    }

    private static readonly HashSet<string> ExcludedMethods =
    [
        "<Clone>$",
        "Deconstruct",
        "ToString",
        "Equals",
        "GetHashCode"
    ];

}
