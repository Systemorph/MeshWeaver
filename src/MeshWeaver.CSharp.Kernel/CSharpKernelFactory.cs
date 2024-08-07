using System.Reflection;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Collections;

namespace MeshWeaver.CSharp.Kernel
{
    public class CSharpKernelFactory
    {
        private readonly IServiceProvider serviceProvider;

        public CSharpKernelFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }


        public CSharpKernel CreateKernel()
        {
            var kernel = new CSharpKernel();
            //kernel.AddUsings(nameof(System),
            //                 "System.Linq",
            //                 "System.Collections.Generic",
            //                 "System.Threading.Tasks");
            //kernel.AddReferences(DefaultAssemblies);

            //kernel.RespectInitialSessionVariables();

            return kernel;
        }

        private static readonly Assembly[] DefaultAssemblies =
        {
            //Assembly.Load("netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"),
            //typeof(Enumerable).Assembly,
            //typeof(MethodInfo).Assembly,
            //typeof(CreatableObjectStore<,>).Assembly,
            //// TODO V10: this is in the wrong assembly (19.08.2020, Roland Buergi)
            //typeof(Enumerable).Assembly,
            //typeof(List<>).Assembly,
        };
    }
}