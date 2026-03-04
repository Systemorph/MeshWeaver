using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting;

/// <summary>
/// Extension methods for selectively including sample data partitions from samples/Graph/Data.
/// Each method registers a partition for inclusion when using AddPartitionedFileSystemPersistence.
/// If no partition methods are called, all partitions are loaded (backward compatibility).
/// </summary>
public static class SampleDataExtensions
{
    // === Domain data partitions ===

    /// <summary>Includes the Northwind sample data (customers, products, employees, orders).</summary>
    public static TBuilder AddNorthwind<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Northwind");

    /// <summary>Includes the FutuRe insurance/reinsurance sample data.</summary>
    public static TBuilder AddFutuRe<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("FutuRe");

    /// <summary>Includes the ACME project management sample data.</summary>
    public static TBuilder AddAcme<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("ACME");

    /// <summary>Includes the Cornerstone insurance sample data.</summary>
    public static TBuilder AddCornerstone<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Cornerstone");

    /// <summary>Includes the Organization sample data.</summary>
    public static TBuilder AddOrganization<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Organization");

    /// <summary>Includes the Systemorph sample data.</summary>
    public static TBuilder AddSystemorph<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Systemorph");

    /// <summary>Includes the MeshWeaver documentation data.</summary>
    public static TBuilder AddMeshWeaverDocs<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("MeshWeaver");

    /// <summary>Includes the Doc sample data.</summary>
    public static TBuilder AddDoc<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Doc");

    // === Infrastructure partitions ===

    /// <summary>Includes the Admin partition.</summary>
    public static TBuilder AddAdmin<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Admin");

    /// <summary>Includes the Agent partition.</summary>
    public static TBuilder AddAgent<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Agent");

    /// <summary>Includes the ApiToken partition.</summary>
    public static TBuilder AddApiToken<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("ApiToken");

    /// <summary>Includes the Type definitions partition.</summary>
    public static TBuilder AddTypeData<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("Type");

    /// <summary>Includes the User data partition.</summary>
    public static TBuilder AddUserData<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("User");

    /// <summary>Includes the VUser (virtual user) data partition.</summary>
    public static TBuilder AddVUser<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("VUser");

    /// <summary>Includes the kernel data partition.</summary>
    public static TBuilder AddKernelData<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("kernel");

    /// <summary>Includes the activity tracking partition.</summary>
    public static TBuilder AddActivity<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("_activity");

    /// <summary>Includes the user activity tracking partition.</summary>
    public static TBuilder AddUserActivity<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder.IncludePartition("_useractivity");

    // === Convenience bundles ===

    /// <summary>
    /// Includes all sample data partitions.
    /// Equivalent to calling each individual Add method.
    /// </summary>
    public static TBuilder AddAllSampleData<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
        => builder
            .AddNorthwind()
            .AddFutuRe()
            .AddAcme()
            .AddCornerstone()
            .AddOrganization()
            .AddSystemorph()
            .AddMeshWeaverDocs()
            .AddDoc()
            .AddAdmin()
            .AddAgent()
            .AddApiToken()
            .AddTypeData()
            .AddUserData()
            .AddVUser()
            .AddKernelData()
            .AddActivity()
            .AddUserActivity();
}
