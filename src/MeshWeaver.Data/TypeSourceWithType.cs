using System.Collections.Immutable;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

public record PartitionedTypeSourceWithType<T, TPartition>(
    IWorkspace Workspace,
    Func<T, TPartition> PartitionFunction,
    object DataSource
) : TypeSourceWithType<T>(Workspace, DataSource), IPartitionedTypeSource
{
    public object GetPartition(object instance) => PartitionFunction.Invoke((T)instance) ?? new object();
}

public record TypeSourceWithType<T>(IWorkspace Workspace, object DataSource)
    : TypeSourceWithType<T, TypeSourceWithType<T>>(Workspace, DataSource)
{
    protected override InstanceCollection UpdateImpl(InstanceCollection instances) =>
        UpdateAction.Invoke(instances);

    protected Func<InstanceCollection, InstanceCollection> UpdateAction { get; init; } = i => i;

    public TypeSourceWithType<T> WithUpdate(Func<InstanceCollection, InstanceCollection> update) =>
        This with
        {
            UpdateAction = update
        };

    public TypeSourceWithType<T> WithInitialData(
        Func<CancellationToken, Task<IEnumerable<T>>> initialData
    ) => WithInitialData(async (_, c) => (await initialData.Invoke(c)).Cast<object>());

    public TypeSourceWithType<T> WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<T>>
        > initialData
    ) => WithInitialData(async (r, c) => (await initialData.Invoke(r, c)).Cast<object>());

    public TypeSourceWithType<T> WithInitialData(IEnumerable<T> initialData) =>
        WithInitialData((_, _) => Task.FromResult(initialData.Cast<object>()));

}

public abstract record TypeSourceWithType<T, TTypeSource>(IWorkspace Workspace, object DataSource)
    : TypeSource<TTypeSource>(Workspace, typeof(T))
    where TTypeSource : TypeSourceWithType<T, TTypeSource>
{
    public TTypeSource WithQuery(Func<string, T?> query) => This with { QueryFunction = query };

    protected Func<string, T?>? QueryFunction { get; init; }

    public TTypeSource WithKey<TProp>(Func<T, TProp> keyFunc)
        => WithKey(new KeyFunction(o => keyFunc.Invoke((T)o)!, typeof(TProp)));

    /// <summary>
    /// Adds an access restriction for this type using async evaluation.
    /// </summary>
    /// <param name="restriction">Async restriction delegate to evaluate</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated type source with the restriction added</returns>
    public TTypeSource WithAccessRestriction(
        AccessRestrictionDelegate restriction,
        string? name = null)
    {
        return This with
        {
            AccessRestrictions = AccessRestrictions.Add(new AccessRestrictionEntry(restriction, name))
        };
    }

    /// <summary>
    /// Adds a strongly-typed access restriction for row-level operations.
    /// The entity is cast to T before being passed to the restriction.
    /// </summary>
    /// <param name="restriction">Strongly-typed restriction function</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated type source with the restriction added</returns>
    /// <example>
    /// <code>
    /// .WithType&lt;OwnedEntity&gt;(type => type
    ///     .WithTypedAccessRestriction((action, entity, ctx) =>
    ///         action == "Read" || entity.OwnerId == ctx.UserContext?.ObjectId,
    ///         "OwnerOnly"))
    /// </code>
    /// </example>
    public TTypeSource WithTypedAccessRestriction(
        Func<string, T, AccessRestrictionContext, bool> restriction,
        string? name = null)
    {
        return WithAccessRestriction(
            (action, ctx, accessCtx) =>
            {
                if (ctx is T instance)
                    return Task.FromResult(restriction(action, instance, accessCtx));
                return Task.FromResult(true); // Allow if not the right type (shouldn't happen for instance-level checks)
            },
            name);
    }

    /// <summary>
    /// Adds a strongly-typed async access restriction for row-level operations.
    /// </summary>
    /// <param name="restriction">Strongly-typed async restriction function</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated type source with the restriction added</returns>
    public TTypeSource WithTypedAccessRestriction(
        Func<string, T, AccessRestrictionContext, Task<bool>> restriction,
        string? name = null)
    {
        return WithAccessRestriction(
            async (action, ctx, accessCtx) =>
            {
                if (ctx is T instance)
                    return await restriction(action, instance, accessCtx);
                return true;
            },
            name);
    }
}
