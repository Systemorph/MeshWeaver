using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for configuring which types can be created from a node.
/// </summary>
public static class CreatableTypesExtensions
{
    /// <summary>
    /// Adds a rule for which types can be created from instances of this node type.
    /// Multiple rules can be added and their results are combined.
    /// </summary>
    /// <param name="config">The message hub configuration.</param>
    /// <param name="getCreatableTypes">
    /// Function that receives the parent MeshNode (null for root) and returns type paths that can be created.
    /// </param>
    /// <returns>The configuration for chaining.</returns>
    /// <example>
    /// // Organization allows creating Projects and NodeTypes
    /// config => config
    ///     .WithContentType&lt;Organization&gt;()
    ///     .AddCreatableTypes(parent => ["ACME/Project", "NodeType"])
    ///
    /// // Project allows creating Todos
    /// config => config
    ///     .WithContentType&lt;Project&gt;()
    ///     .AddCreatableTypes(parent => ["ACME/Project/Todo"])
    ///
    /// // Todo allows recursive nesting
    /// config => config
    ///     .WithContentType&lt;Todo&gt;()
    ///     .AddCreatableTypes(parent => ["ACME/Project/Todo"])
    ///
    /// // Dynamic based on parent properties
    /// config => config
    ///     .AddCreatableTypes(parent => parent?.NodeType == "ACME/Project"
    ///         ? ["ACME/Project/Todo", "ACME/Project/Story"]
    ///         : [])
    /// </example>
    public static MessageHubConfiguration AddCreatableTypes(
        this MessageHubConfiguration config,
        Func<MeshNode?, IEnumerable<string>> getCreatableTypes)
    {
        var existing = config.Get<CreatableTypesRules>() ?? new CreatableTypesRules();
        var updated = existing.Add(getCreatableTypes);
        return config.Set(updated);
    }

    /// <summary>
    /// Adds static creatable types (convenience overload).
    /// </summary>
    public static MessageHubConfiguration AddCreatableTypes(
        this MessageHubConfiguration config,
        params string[] types)
    {
        return config.AddCreatableTypes(_ => types);
    }

    /// <summary>
    /// Clears the default global creatable types (Markdown, Thread, Agent, NodeType).
    /// Call this before AddCreatableTypes to have full control over what's creatable.
    /// </summary>
    public static MessageHubConfiguration ClearDefaultCreatableTypes(
        this MessageHubConfiguration config)
    {
        var existing = config.Get<CreatableTypesRules>() ?? new CreatableTypesRules();
        return config.Set(existing with { IncludeDefaults = false });
    }

    /// <summary>
    /// Excludes specific types from being creatable (removes them from defaults and any added rules).
    /// </summary>
    public static MessageHubConfiguration ExcludeCreatableTypes(
        this MessageHubConfiguration config,
        params string[] types)
    {
        var existing = config.Get<CreatableTypesRules>() ?? new CreatableTypesRules();
        var updated = existing with
        {
            ExcludedTypes = existing.ExcludedTypes.Concat(types).Distinct().ToList()
        };
        return config.Set(updated);
    }

    /// <summary>
    /// Marks this type as not creatable in any menu (code-only creation).
    /// This is configured on the TYPE being created, not the parent.
    /// </summary>
    public static MessageHubConfiguration NotCreatable(
        this MessageHubConfiguration config)
    {
        return config.Set(new NotCreatableMarker());
    }

    /// <summary>
    /// Gets the creatable types rules from configuration.
    /// </summary>
    internal static CreatableTypesRules? GetCreatableTypesRules(this MessageHubConfiguration config)
    {
        return config.Get<CreatableTypesRules>();
    }

    /// <summary>
    /// Checks if a type is marked as not creatable.
    /// </summary>
    internal static bool IsNotCreatable(this MessageHubConfiguration config)
    {
        return config.Get<NotCreatableMarker>() != null;
    }
}

/// <summary>
/// Holds the accumulated rules for creatable types.
/// </summary>
public record CreatableTypesRules
{
    /// <summary>
    /// List of functions that return creatable type paths for a given parent.
    /// </summary>
    public IReadOnlyList<Func<MeshNode?, IEnumerable<string>>> Rules { get; init; } = [];

    /// <summary>
    /// Whether to include default global types (Markdown, Thread, Agent, NodeType).
    /// </summary>
    public bool IncludeDefaults { get; init; } = true;

    /// <summary>
    /// Types to exclude from the final list.
    /// </summary>
    public IReadOnlyList<string> ExcludedTypes { get; init; } = [];

    /// <summary>
    /// Adds a rule to the list.
    /// </summary>
    public CreatableTypesRules Add(Func<MeshNode?, IEnumerable<string>> rule)
    {
        return this with { Rules = Rules.Append(rule).ToList() };
    }

    /// <summary>
    /// Evaluates all rules for a given parent node and returns the combined creatable types.
    /// </summary>
    public IEnumerable<string> GetCreatableTypes(MeshNode? parent, IEnumerable<string> defaultTypes)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add defaults if included
        if (IncludeDefaults)
        {
            foreach (var t in defaultTypes)
                types.Add(t);
        }

        // Add types from all rules
        foreach (var rule in Rules)
        {
            foreach (var t in rule(parent))
                types.Add(t);
        }

        // Remove excluded types
        foreach (var excluded in ExcludedTypes)
            types.Remove(excluded);

        return types;
    }
}

/// <summary>
/// Marker to indicate a type is not creatable via UI.
/// </summary>
internal record NotCreatableMarker;
