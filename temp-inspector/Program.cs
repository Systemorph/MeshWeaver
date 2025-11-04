using System;
using System.Reflection;
using System.Linq;

var assembly = Assembly.LoadFrom(@"C:\Users\RolandBuergi\.nuget\packages\microsoft.agents.ai\1.0.0-preview.251001.3\lib\net9.0\Microsoft.Agents.AI.dll");

Console.WriteLine("=== Agent-related Types ===");
foreach (var type in assembly.GetExportedTypes().Where(t => t.Name.Contains("Agent", StringComparison.OrdinalIgnoreCase) && t.IsPublic).OrderBy(t => t.Name))
{
    Console.WriteLine($"{type.FullName} (IsInterface: {type.IsInterface}, IsClass: {type.IsClass})");
}

Console.WriteLine("\n=== Extension Methods ===");
foreach (var type in assembly.GetExportedTypes().Where(t => t.IsPublic && t.IsClass && t.IsSealed && t.IsAbstract))
{
    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
    foreach (var method in methods.OrderBy(m => m.Name))
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"{type.Name}.{method.Name}({parameters}) -> {method.ReturnType.Name}");
    }
}

Console.WriteLine("\n=== All Public Interfaces ===");
foreach (var type in assembly.GetExportedTypes().Where(t => t.IsPublic && t.IsInterface).OrderBy(t => t.Name))
{
    Console.WriteLine($"{type.FullName}");
}

Console.WriteLine("\n=== ChatClientAgent Details ===");
var chatClientAgentType = assembly.GetExportedTypes().FirstOrDefault(t => t.Name == "ChatClientAgent");
if (chatClientAgentType != null)
{
    Console.WriteLine($"Base Type: {chatClientAgentType.BaseType?.FullName}");
    Console.WriteLine("Constructors:");
    foreach (var ctor in chatClientAgentType.GetConstructors())
    {
        var parameters = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  ChatClientAgent({parameters})");
    }
}

Console.WriteLine("\n=== AIAgentBuilder Details ===");
var builderType = assembly.GetExportedTypes().FirstOrDefault(t => t.Name == "AIAgentBuilder");
if (builderType != null)
{
    Console.WriteLine("Constructors:");
    foreach (var ctor in builderType.GetConstructors())
    {
        var parameters = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  AIAgentBuilder({parameters})");
    }
    Console.WriteLine("\nWith* Methods:");
    foreach (var method in builderType.GetMethods().Where(m => m.Name.StartsWith("With")).Take(10))
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {method.Name}({parameters}) -> {method.ReturnType.Name}");
    }
}
