namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The NAMED refusal behind <see cref="PrebuiltAssemblySeeder.RequirePrebuiltConfigKey"/>: this
/// mesh requires prebuilt module assemblies, none could be resolved for the lane, and compiling is
/// not a fallback here. The message always says WHAT was missing (package, registry, framework
/// identity/architecture, the miss kind) and WHAT fixes it (publish or rebake the bundle for this
/// lane) — a distribution failure must read as a distribution failure, never as a build one four
/// causal steps later (the 2026-08-25 incident shape; design of record Systemorph/MeshWeaver#2193
/// §A).
/// </summary>
public sealed class PrebuiltRequiredException(string message) : InvalidOperationException(message);
