using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;

namespace MeshWeaver.Connection.Notebook;

public class ProxyKernel : Kernel,
    IKernelCommandHandler<RequestCompletions>,
    IKernelCommandHandler<RequestDiagnostics>,
    IKernelCommandHandler<RequestHoverText>,
    IKernelCommandHandler<RequestSignatureHelp>,
    IKernelCommandHandler<RequestValue>,
    IKernelCommandHandler<RequestValueInfos>,
    IKernelCommandHandler<SendValue>,
    IKernelCommandHandler<SubmitCode>
{
    private readonly string addressId;
    private readonly string addressType;
    private readonly Kernel kernel;

    public ProxyKernel(string name, Kernel kernel, IMessageHub hub) : base(name)
    {
        addressId = hub.Address.ToString();
        addressType = hub.GetTypeRegistry().GetCollectionName(hub.Address.GetType());
        this.kernel = kernel;
        kernel.KernelEvents.Subscribe(OnInnerKernelEvent);
    }

    public async Task HandleAsync(RequestCompletions command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<RequestCompletions> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(RequestDiagnostics command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<RequestDiagnostics> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(RequestHoverText command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<RequestHoverText> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(RequestSignatureHelp command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<RequestSignatureHelp> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(RequestValue command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<RequestValue> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(RequestValueInfos command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<RequestValueInfos> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(SendValue command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<SendValue> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }

    public async Task HandleAsync(SubmitCode command, KernelInvocationContext context)
    {
        if (kernel is IKernelCommandHandler<SubmitCode> handler)
        {
            await handler.HandleAsync(command, context);
        }
    }



    private void OnInnerKernelEvent(KernelEvent kernelEvent)
    {
        // Filter or modify the events here
        if (kernelEvent is ReturnValueProduced 
                { Value: UiControl control } returnValueProduced
                )
        {
            var id = Guid.NewGuid().AsString();
            cells[id] = control;
            var url = new LayoutAreaReference(nameof(MeshClient.Cell)) { Id = id }.ToHref(addressType, addressId);
            var htmlContent = $"<iframe src='{url}'></iframe>";
            var newEvent = new DisplayedValueProduced(htmlContent, returnValueProduced.Command, returnValueProduced.FormattedValues);

            PublishEvent(newEvent);
            return;
        }

        PublishEvent(kernelEvent);
    }

    private readonly ConcurrentDictionary<string, UiControl> cells = new();

    public UiControl GetCell(string id)
        => cells.GetValueOrDefault(id);

}
