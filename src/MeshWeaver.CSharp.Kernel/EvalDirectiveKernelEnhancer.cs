//using System.CommandLine;
//using System.CommandLine.NamingConventionBinder;
//using Microsoft.DotNet.Interactive;
//using Microsoft.DotNet.Interactive.Commands;
//using Microsoft.DotNet.Interactive.CSharp;
//using Microsoft.DotNet.Interactive.Events;

//namespace MeshWeaver.CSharp.Kernel
//{
//    public class EvalDirectiveKernelEnhancer : IKernelEnhancer
//    {
//        public void Enhance(CSharpKernel kernel)
//        {
//            var evalDirective = new Command("#!eval")
//                                {
//                                    new Argument<string>("expression", "Expression to be evaluated")
//                                };

//            evalDirective.Handler = CommandHandler.Create<string, KernelInvocationContext>(HandleEval);

//            kernel.AddDirective(evalDirective);

//            async Task HandleEval(string expression, KernelInvocationContext context)
//            {
//                var rootCommand = context.Command.GetRootCommand();
//                if (rootCommand.Properties.TryGetValue(CSharpKernel.StopPropagation, out var stopPropagation) && (bool)stopPropagation)
//                    return;

//                try
//                {
//                    using var childKernel = kernel.CreateChild();

//                    var expressionCommand = new SubmitCode(expression);
//                    await childKernel.SendAsync(expressionCommand);
//                    var expressionFailed = false;
//                    using var subscription = kernel.KernelEvents.Subscribe(x =>
//                    {
//                        if (x is (CSharpCommandFailed or CommandFailed))
//                        {
//                            expressionFailed = true;
//                        }
//                    });
//                    if (expressionFailed)
//                        return;

//                    var executedExpression = (string)childKernel.ScriptState.ReturnValue;
//                    var expressionEvaluationCommand = new SubmitCode(executedExpression);
//                    await childKernel.SendAsync(expressionEvaluationCommand);

//                    kernel.TakeStateOfChild(childKernel);
//                }
//                catch (Exception e)
//                {
//                    rootCommand.Properties[CSharpKernel.StopPropagation] = true;
//                    context.Publish(new CSharpCommandFailed(context.Command, e, e.Message));
//                }
//            }
//        }
//    }
//}