using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace MeshWeaver.Observability;

/// <summary>
/// Configuration for <b>LogIncident</b> nodes — one per distinct red-log fingerprint, living in
/// the platform-level <c>Admin</c> partition at <c>Admin/_LogIncident/{fingerprint}</c>.
///
/// <para>Admin-scoped by design: a red log is platform state, not any user's data, and the
/// incident carries stack traces and message text from every partition. Reading it is therefore a
/// platform-admin capability (<c>hub.IsGlobalAdmin()</c>), never a per-user one — which is why
/// this type is NOT registered with <c>WithPublicRead</c>.</para>
/// </summary>
public static class LogIncidentNodeType
{
    /// <summary>The NodeType value used to identify log-incident nodes.</summary>
    public const string NodeType = "LogIncident";

    /// <summary>The partition incidents live in — platform state, not user data.</summary>
    public const string PartitionName = "Admin";

    /// <summary>The satellite segment holding incidents: <c>Admin/_LogIncident</c>.</summary>
    public const string SatelliteSegment = "_LogIncident";

    /// <summary>The namespace every incident node lives in: <c>Admin/_LogIncident</c>.</summary>
    public const string IncidentNamespace = $"{PartitionName}/{SatelliteSegment}";

    /// <summary>The node path for one fingerprint: <c>Admin/_LogIncident/{fingerprint}</c>.</summary>
    public static string IncidentPath(string fingerprint) => $"{IncidentNamespace}/{fingerprint}";

    /// <summary>Registers the built-in "LogIncident" MeshNode on the mesh builder.</summary>
    public static TBuilder AddLogIncidentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the LogIncident node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Log Incident",
        Icon = "/static/NodeTypeIcons/bug.svg",
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<LogIncident>())
    };
}
