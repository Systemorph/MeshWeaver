using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.CSharp;
using MeshWeaver.Messaging;

namespace MeshWeaver.Notebooks.Hub;


public class KernelHost : IKernelHost
{
    private readonly Kernel csharpKernel;
    private readonly IMessageHub hub;

    public KernelHost(IMessageHub hub)
    {
        csharpKernel = new CSharpKernel();

        // Subscribe to kernel events
        csharpKernel.KernelEvents.Subscribe(HandleKernelEvent);
        this.hub = hub;
    }

    public async Task SubmitCode(string code)
    {
        try
        {
            var command = new SubmitCode(code);
            await csharpKernel.SendAsync(command);
        }
        catch (Exception ex)
        {
            hub.Post(new ErrorMessage(ex.Message));
        }
    }
    // Add these methods to NotebookKernel class
    public async Task RequestCompletion(string code, int line, int character)
    {
        try
        {
            var request = new RequestCompletions(code, new(line, character));
            await csharpKernel.SendAsync(request);
        }
        catch (Exception ex)
        {
            hub.Post(new ErrorMessage(ex.Message));
        }
    }
    private void HandleKernelEvent(KernelEvent kernelEvent)
    {
        switch (kernelEvent)
        {
            case ReturnValueProduced result:
                hub.Post(new ResultMessage(result.Value?.ToString() ?? "null"));
                break;

            case CompletionsProduced completions:
                var items = completions.Completions.Select(c => c.DisplayText);
                hub.Post(new CompletionResponseMessage(items));
                break;

            case StandardOutputValueProduced output:
                hub.Post(new ResultMessage(output.Value));
                break;

            case CommandFailed error:
                hub.Post(new ErrorMessage(error.Message));
                break;
        }
    }
}
