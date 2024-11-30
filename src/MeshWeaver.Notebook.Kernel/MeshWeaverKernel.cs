using System.Security.Cryptography.X509Certificates;
using MeshWeaver.Messaging;
using MeshWeaver.Notebooks;
using Microsoft.DotNet.Interactive;

namespace MeshWeaver.Notebook.Kernel;
internal partial class MeshWeaverKernel : Microsoft.DotNet.Interactive.Kernel
{
    private IMessageHub Hub { get; }
    private object Address { get; }
    protected MeshWeaverKernel(string name, IMessageHub hub, object address, string languageName, string languageVersion)
        : base(name)
    {
        KernelInfo.LanguageName = languageName;
        KernelInfo.LanguageVersion = languageVersion;
        KernelInfo.DisplayName = $"{name} - {languageName} {languageVersion} (Preview)";
        Hub = hub;
        Address = address;

    }



    public static async Task<MeshWeaverKernel> CreateAsync(string name, IMessageHub hub, object address)
    {

        // request kernel info
        var kernelInfo = await RequestKernelInfo(hub, address);

        return new MeshWeaverKernel(name,
        hub,
        address,
                                 kernelInfo?.Language,
                                 kernelInfo?.LanguageVersion);
    }

    private static async Task<KernelInfoReply> RequestKernelInfo(IMessageHub hub, object address)
    {
        var request = new KernelInfoRequest();
        var kernelInfoReply = await RunOnKernelAsync(request,
                                                                      hub,
                                                                      address,
                                                                      CancellationToken.None);

        return (KernelInfoReply)kernelInfoReply;
    }


    public async Task<ReplyMessage> RunOnKernelAsync(string code, CancellationToken cancellationToken = default)
    {
        var request = new ExecuteRequest(code, Name);
        var reply = await RunOnKernelAsync(request, Hub, Address, cancellationToken);

        return reply;
    }
    public static async Task<ReplyMessage> RunOnKernelAsync<T>(
        T content,
        IMessageHub hub,
        object address,
        CancellationToken cancellationToken)
        where T : IRequest<ReplyMessage>
    {

        var delivery = await hub.AwaitResponse(content, o => o.WithTarget(address), cancellationToken);

        //TODO Roland Bürgi 2024-11-29: Handle errors
        return delivery.Message;

    }
}

