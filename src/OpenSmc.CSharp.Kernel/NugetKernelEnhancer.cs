using Microsoft.DotNet.Interactive.CSharp;

namespace OpenSmc.CSharp.Kernel
{
    public class NugetKernelEnhancer : IKernelEnhancer
    {
        public void Enhance(CSharpKernel kernel)
        {
            //kernel.UseNugetDirective();
        }
    }
}