using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The two step-up node types — <see cref="StepUpPaths.ReceiptNodeType"/> (a minted receipt, at
/// <c>Auth/_StepUp/{id}</c>) and <see cref="StepUpPaths.ConsumptionNodeType"/> (one target's
/// consumption marker, at <c>Auth/_StepUpUse/{id}-{key}</c>) — and the platform
/// <see cref="IStepUpService"/>. Both types are System-only for every operation: the step-up
/// endpoint mints, consumers consume through the service, and nobody else reads or writes them.
/// See <c>Doc/Architecture/ApprovalStepUp</c>.
/// </summary>
public static class StepUpNodeTypes
{
    /// <summary>Registers both node types, their access rule and the step-up service.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddStepUpTypes<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(
            new MeshNode(StepUpPaths.ReceiptNodeType)
            {
                Name = "Step-up receipt",
                Icon = "/static/NodeTypeIcons/key.svg",
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<StepUpReceipt>())
            },
            new MeshNode(StepUpPaths.ConsumptionNodeType)
            {
                Name = "Step-up consumption",
                Icon = "/static/NodeTypeIcons/key.svg",
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<StepUpConsumption>())
            },
            new MeshNode(StepUpPaths.FactorsNodeType)
            {
                Name = "Step-up factors",
                Icon = "/static/NodeTypeIcons/key.svg",
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<StepUpFactors>())
            },
            new MeshNode(StepUpPaths.PendingNodeType)
            {
                Name = "Step-up in progress",
                Icon = "/static/NodeTypeIcons/key.svg",
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<StepUpPending>())
            });
        // Every hub must know the discriminators, not only the per-node hubs above (#2729): a
        // reader elsewhere whose TypeRegistry lacks them gets a raw JsonElement — a silent null.
        builder.ConfigureHub(config => config
            .WithType<StepUpReceipt>(nameof(StepUpReceipt))
            .WithType<StepUpConsumption>(nameof(StepUpConsumption))
            .WithType<StepUpPending>(nameof(StepUpPending))
            .WithType<StepUpFactors>(nameof(StepUpFactors)));
        builder.AddAutocompleteExcludedTypes(StepUpPaths.ReceiptNodeType, StepUpPaths.ConsumptionNodeType, StepUpPaths.PendingNodeType, StepUpPaths.FactorsNodeType);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<INodeTypeAccessRule>(new SystemOnlyAccessRule(StepUpPaths.ReceiptNodeType));
            s.AddSingleton<INodeTypeAccessRule>(new SystemOnlyAccessRule(StepUpPaths.ConsumptionNodeType));
            s.AddSingleton<INodeTypeAccessRule>(new SystemOnlyAccessRule(StepUpPaths.PendingNodeType));
            s.AddSingleton<INodeTypeAccessRule>(new SystemOnlyAccessRule(StepUpPaths.FactorsNodeType));
            // The concrete service is INTERNAL (it alone can mint); consumers get the interface.
            s.TryAddSingleton<StepUpService>();
            s.TryAddSingleton<IStepUpService>(sp => sp.GetRequiredService<StepUpService>());
            return s;
        });
        return builder;
    }

    /// <summary>
    /// Admits <see cref="WellKnownUsers.System"/> alone, for every operation. A receipt is proof of
    /// a fresh authentication; a user who could create, edit or delete one — or a marker — could
    /// forge or replay it. The seal covers the receipt even past this rule; the rule keeps the
    /// markers (which carry no seal) honest.
    /// </summary>
    /// <param name="nodeType">The governed node type.</param>
    internal sealed class SystemOnlyAccessRule(string nodeType) : INodeTypeAccessRule
    {
        /// <inheritdoc />
        public string NodeType => nodeType;

        /// <inheritdoc />
        public IReadOnlyCollection<NodeOperation> SupportedOperations =>
            [NodeOperation.Read, NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

        /// <inheritdoc />
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) =>
            Observable.Return(userId == WellKnownUsers.System);
    }
}
