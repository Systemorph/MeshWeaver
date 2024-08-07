
using Microsoft.DotNet.Interactive.CSharp;

namespace MeshWeaver.CSharp.Kernel
{
    public interface IKernelEnhancer
    {
        void Enhance(CSharpKernel kernel);
    }
}
