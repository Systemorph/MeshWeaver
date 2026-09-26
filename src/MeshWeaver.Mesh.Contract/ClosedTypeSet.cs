using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Whether this process serves a CLOSED type set: every NodeType it can activate is one the image
/// registered IN CODE (an <see cref="Services.IStaticNodeProvider"/> node carrying a
/// <see cref="MeshNode.HubConfiguration"/> — a module's <see cref="MeshNodeProviderAttribute"/>, or
/// <see cref="MeshBuilder.AddMeshNodes"/>), and no type definition is ever read from the database,
/// compiled, or adopted from a bundle.
///
/// <para><b>Why it exists.</b> A portal's readiness and its ability to activate a node both
/// depended on content in its database that its image did not ship: one abandoned in-mesh type in
/// a user partition refused readiness on every pod of the control instance for seven hours
/// (Memex <c>docs/control-instance.md</c> §1). The control instance's answer is a property — P1,
/// <i>every NodeType it can activate is defined by the image</i> — and this switch is the platform
/// half of it: with it on, a node whose type is not registered in code is refused, NAMED, at
/// activation instead of being resolved from a database row; the pre-warm sweep and the bake probe
/// enumerate nothing; and a compile request on a database NodeType row is parked with the same
/// named reason instead of reaching Roslyn. Records remain records — a malformed one is refused as
/// a record — but no row can change which types exist.</para>
///
/// <para>Off by default: every user portal serves in-mesh types, and closing the set there would
/// strand them. A host opts in with <see cref="ClosedTypeSetExtensions.WithClosedTypeSet"/> or with
/// <see cref="ConfigKey"/><c>=true</c> in its configuration (the control image sets it as a
/// container environment variable, so the property travels with the image rather than a record).</para>
/// </summary>
/// <param name="IsClosed"><c>true</c> when only code-registered NodeTypes may be activated.</param>
public sealed record ClosedTypeSet(bool IsClosed)
{
    /// <summary>The configuration key that closes the set (<c>Mesh__ClosedTypeSet</c> as an environment variable).</summary>
    public const string ConfigKey = "Mesh:ClosedTypeSet";

    /// <summary>The default: database-defined NodeTypes resolve, compile and adopt as usual.</summary>
    public static readonly ClosedTypeSet Open = new(false);

    /// <summary>Only NodeTypes registered in code by the image may be activated.</summary>
    public static readonly ClosedTypeSet Closed = new(true);

    /// <summary>
    /// The named refusal a closed mesh gives for a type the image does not register — one sentence,
    /// shared by the activation overlay, the compile park and the logs, so every surface reports the
    /// same cause.
    /// </summary>
    /// <param name="nodeType">The type that was asked for.</param>
    /// <param name="instancePath">The node that asked for it, when there is one.</param>
    public static string RefusalFor(string nodeType, string? instancePath = null) =>
        $"NodeType '{nodeType}' is not part of this image's closed type set ({ConfigKey}=true)"
        + (string.IsNullOrEmpty(instancePath) ? "" : $" (referenced by '{instancePath}')")
        + ". This process activates only NodeTypes its image registers in code; a type definition "
        + "stored in the database is never compiled, adopted or served here. Register the type in "
        + "the image's module, or open this node on a portal that serves in-mesh types.";
}

/// <summary>Registration and lookup for <see cref="ClosedTypeSet"/>.</summary>
public static class ClosedTypeSetExtensions
{
    /// <summary>
    /// Closes this mesh's type set: only NodeTypes registered in code may be activated (see
    /// <see cref="ClosedTypeSet"/>). Wins over configuration.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    public static MeshBuilder WithClosedTypeSet(this MeshBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton(ClosedTypeSet.Closed));

    /// <summary>
    /// True when this process serves a closed type set — a registered <see cref="ClosedTypeSet"/>
    /// decides; otherwise <see cref="ClosedTypeSet.ConfigKey"/> in the process configuration; otherwise
    /// open. An unparsable value is OPEN, the same answer an absent key gives: closing the set
    /// withdraws types, so it is never inferred from a value nobody meant.
    /// </summary>
    /// <param name="services">A hub's service provider.</param>
    public static bool IsClosedTypeSet(this IServiceProvider services)
    {
        if (services.GetService<ClosedTypeSet>() is { } declared)
            return declared.IsClosed;
        return services.GetService<IConfiguration>() is { } configuration
            && bool.TryParse(configuration[ClosedTypeSet.ConfigKey], out var closed)
            && closed;
    }
}
