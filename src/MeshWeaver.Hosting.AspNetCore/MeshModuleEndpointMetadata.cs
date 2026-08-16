namespace MeshWeaver.Hosting.AspNetCore;

/// <summary>
/// Marker metadata stamped on every endpoint a module contributes through
/// <see cref="MeshModuleEndpointExtensions.MapMeshModuleEndpoints"/>. The collision refusal keys
/// on it: only duplicate (verb, pattern) groups that involve at least one module-contributed
/// endpoint are a refusal. The platform's own endpoint table legitimately carries duplicates —
/// a published app's static-asset endpoints register one endpoint per precompressed variant
/// (identity/gzip/brotli) on the SAME route, disambiguated by content negotiation — so a
/// whole-table duplicate assertion refuses every published deployment while passing every dev run.
/// </summary>
/// <param name="ModuleName">The contributing module's assembly name, surfaced in the refusal.</param>
public sealed record MeshModuleEndpointMetadata(string ModuleName);
