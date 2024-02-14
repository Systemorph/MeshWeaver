using System.Collections.Immutable;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using OpenSmc.Hub.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Orleans.Test
{
    public class Class1(ITestOutputHelper toh) : HubTestBase(toh)
    {
        [Fact]
        public async Task TestMethod()
        {
            ImmutableArray<ScriptVariable> variablesAfterEvaluation;
            ImmutableArray<ScriptVariable> variable2CountAfterEvaluation;

            var kernel = CreateKernel();
            var location = @"C:\dev\OpenSmc\src\OpenSmc.Arithmetics\bin\Debug\net8.0\OpenSmc.Arithmetics.dll";
            var vals1 = await CreateKernel(
                $"#r \"{location}\"",
                "OpenSmc.Arithmetics.Aggregation.AggregationFunction.Value = 10;",
                "var x = OpenSmc.Arithmetics.Aggregation.AggregationFunction.Value;"

            );
            var vals2 = await CreateKernel(
                $"#r \"{location}\"",
                "var x = OpenSmc.Arithmetics.Aggregation.AggregationFunction.Value;"

            );



        }

        private static async Task<ImmutableArray<ScriptVariable>> CreateKernel(params string[] code)
        {
            ImmutableArray<ScriptVariable> variablesAfterEvaluation = ImmutableArray<ScriptVariable>.Empty;
            using var kernel = new CSharpKernel();

            kernel.AddMiddleware(async (command, context, next) =>
            {
                var k = context.HandlingKernel as CSharpKernel;

                await next(command, context);

                variablesAfterEvaluation = k?.ScriptState?.Variables ?? ImmutableArray<ScriptVariable>.Empty;
            });
            foreach (var s in code)
                await SubmitCode(kernel, s);

            return variablesAfterEvaluation;
        }

        public class CollectibleAssemblyLoadContext() : AssemblyLoadContext(isCollectible: true);

        public static async Task<KernelCommandResult> SubmitCode(Kernel kernel, string submission)
        {
            var command = new SubmitCode(submission);
            return await kernel.SendAsync(command);
        }
    }


}
