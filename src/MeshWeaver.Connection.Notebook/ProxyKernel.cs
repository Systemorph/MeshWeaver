using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.Extensions.DependencyInjection;

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
    private string addressId;
    private string addressType;
    private readonly Kernel kernel;

    public ProxyKernel(string name, Kernel kernel) : base(name)
    {
        this.kernel = kernel;
        kernel.KernelEvents.Subscribe(OnInnerKernelEvent);
    }

    public void Initialize(IMessageHub hub)
    {
        addressType = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetCollectionName(hub.Address.GetType());
        addressId = hub.Address.ToString();
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
            Cells[id] = control;
            var url = new LayoutAreaReference(id).ToHref(addressType, addressId);
            var htmlContent = $"<iframe src='{url}'></iframe>";
            var newEvent = new DisplayedValueProduced(htmlContent, returnValueProduced.Command, returnValueProduced.FormattedValues);

            PublishEvent(newEvent);
            return;
        }

        PublishEvent(kernelEvent);
    }

    internal readonly ConcurrentDictionary<string, UiControl> Cells = new();


}
