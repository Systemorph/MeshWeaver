using System.Reflection;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// DI-registered marker that lets a module ship script templates with confidence
/// that its types are visible to the kernel. Each module that seeds Code template
/// MeshNodes (export, import, NodeType compile, …) registers one
/// <see cref="KernelScriptAssembly"/> per assembly that scripts need to reference.
///
/// <para>The kernel's <see cref="KernelExecutor.EnsureInitialized"/> resolves all
/// instances via <c>IServiceProvider.GetServices&lt;KernelScriptAssembly&gt;</c>
/// and adds each <see cref="Assembly"/> to the Roslyn script options. That way
/// the script's <c>using MyNamespace;</c> directive resolves even if Roslyn's
/// AppDomain scan didn't pick the assembly up (e.g. because nothing else in
/// the host had touched it yet).</para>
///
/// <para>Stateless wrapper — keeps the kernel DI-discoverable without forcing
/// kernel hub config to know about every module's assemblies up-front.</para>
/// </summary>
public sealed record KernelScriptAssembly(Assembly Assembly);
