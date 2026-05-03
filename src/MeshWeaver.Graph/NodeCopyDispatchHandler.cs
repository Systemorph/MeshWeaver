using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Starts a script-driven NodeCopy activity for <see cref="NodeCopyDispatchRequest"/>
/// via <see cref="ScriptDispatch.StartScript{TRequest,TResponse}"/>. Posts back
/// a <see cref="NodeCopyDispatchResponse"/> with the activity path so the
/// caller can subscribe for live progress and the script's return value.
/// Does NOT wait for the activity to finish.
///
/// <para>Stateless static class — registered on the mesh hub config from
/// <c>AddGraph</c>. Per <c>Doc/Architecture/AsynchronousCalls.md</c>
/// → "Static handlers compose — don't wrap them in a service".</para>
/// </summary>
internal static class NodeCopyDispatchHandler
{
    public static MessageHubConfiguration AddNodeCopyDispatchHandler(this MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(NodeCopyDispatchRequest), nameof(NodeCopyDispatchRequest));
        config.TypeRegistry.WithType(typeof(NodeCopyDispatchResponse), nameof(NodeCopyDispatchResponse));
        return config.WithHandler<NodeCopyDispatchRequest>(Handle);
    }

    private static IMessageDelivery Handle(
        IMessageHub hub, IMessageDelivery<NodeCopyDispatchRequest> delivery)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(NodeCopyDispatchHandler).FullName!);
        var request = delivery.Message;
        var jsonOptions = hub.JsonSerializerOptions;

        var inputs = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        inputs["sourcePath"] = JsonSerializer.SerializeToElement(request.SourcePath, jsonOptions);
        inputs["targetNamespace"] = JsonSerializer.SerializeToElement(request.TargetNamespace, jsonOptions);
        inputs["force"] = JsonSerializer.SerializeToElement(request.Force, jsonOptions);

        var templatePath = $"{GraphImportTemplates.TemplatesNamespace}/{GraphImportTemplates.NodeCopyId}";

        return ScriptDispatch.StartScript<NodeCopyDispatchRequest, NodeCopyDispatchResponse>(
            hub,
            delivery,
            templatePath,
            inputs.ToImmutable(),
            mapStarted: started => new NodeCopyDispatchResponse(started.ActivityPath),
            mapFailure: reason => new NodeCopyDispatchResponse("", Error: reason),
            logger: logger);
    }
}
