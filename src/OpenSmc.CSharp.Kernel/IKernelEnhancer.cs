
using Microsoft.DotNet.Interactive.CSharp;

namespace OpenSmc.CSharp.Kernel
{
    public interface IKernelEnhancer
    {
        void Enhance(CSharpKernel kernel);
    }
}
