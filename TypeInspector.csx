using System;
using System.Reflection;
using System.Linq;

var assembly = Assembly.LoadFrom(@"C:\Users\RolandBuergi\.nuget\packages\microsoft.agents.ai\1.0.0-preview.251001.3\lib\net8.0\Microsoft.Agents.AI.dll");

Console.WriteLine("=== Agent-related Types ===");
foreach (var type in assembly.GetExportedTypes().Where(t => t.Name.Contains("Agent", StringComparison.OrdinalIgnoreCase) && t.IsPublic))
{
    Console.WriteLine($"{type.FullName} (IsInterface: {type.IsInterface}, IsClass: {type.IsClass})");
}

Console.WriteLine("\n=== All Public Types ===");
foreach (var type in assembly.GetExportedTypes().Where(t => t.IsPublic).Take(50))
{
    Console.WriteLine($"{type.FullName}");
}
