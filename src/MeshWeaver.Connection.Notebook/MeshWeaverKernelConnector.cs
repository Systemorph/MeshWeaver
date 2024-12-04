using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;

namespace MeshWeaver.Connection.Notebook
{
    public class MeshWeaverKernelConnector(string kernelUrl, IMessageHub hub)
    {
        public async Task<Kernel> CreateKernelAsync(string kernelName)
        {

            var cSharpKernel = await CreateInnerKernelAsync(kernelName);
            var proxyKernel = new ProxyKernel(kernelName, cSharpKernel, hub);
            return proxyKernel;
        }

        private async Task<CSharpKernel> CreateInnerKernelAsync(string kernelName)
        {
            Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

            var csharpKernel = new CSharpKernel(kernelName)
                .UseKernelHelpers()
                .UseValueSharing();

            return csharpKernel;
        }
    }
}
