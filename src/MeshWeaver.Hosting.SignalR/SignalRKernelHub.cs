using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace MeshWeaver.Hosting.SignalR;

public class SignalRKernelHub : Hub
{
    private static readonly ConcurrentDictionary<string, Kernel> Kernels = new();

    public override Task OnConnectedAsync()
    {
        var kernel = CreateKernel();
        Kernels[Context.ConnectionId] = kernel;
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Kernels.TryRemove(Context.ConnectionId, out var kernel))
        {
            kernel.Dispose();
        }
        return base.OnDisconnectedAsync(exception);
    }

    private Kernel CreateKernel()
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var csharpKernel = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing()
            .UseImportMagicCommand();

        var kernel = new CompositeKernel { csharpKernel };
        kernel.DefaultKernelName = csharpKernel.Name;
        return kernel;
    }

    public async Task SendMessage(string user, string message)
    {
        await Clients.All.SendAsync("ReceiveMessage", user, message);
    }

    public async Task ExecuteCode(string code)
    {
        if (Kernels.TryGetValue(Context.ConnectionId, out var kernel))
        {
            var result = await ExecuteKernelCodeAsync(kernel, code);
            await Clients.Caller.SendAsync("ReceiveExecutionResult", result);
        }
    }

    private async Task<string> ExecuteKernelCodeAsync(Kernel kernel, string code)
    {
        var command = new SubmitCode(code);
        var result = await kernel.SendAsync(command);
        return result.ToString();
    }
}

