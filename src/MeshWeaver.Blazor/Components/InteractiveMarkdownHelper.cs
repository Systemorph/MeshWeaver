using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Shared helper for interactive markdown execution.
/// Used by both MarkdownView and CollaborativeMarkdownView.
/// Kernel hubs are created on demand by the mesh routing rule
/// (RouteAddressToHostedHub in KernelNodeType.AddKernel).
/// </summary>
internal static class InteractiveMarkdownHelper
{
    /// <summary>
    /// Submits all code requests to the kernel address.
    /// The mesh routing rule creates the kernel hub on demand.
    /// </summary>
    public static void SubmitCode(
        IMessageHub senderHub,
        Address kernelAddress,
        IReadOnlyCollection<SubmitCodeRequest> submissions)
    {
        foreach (var submission in submissions)
            senderHub.Post(submission, o => o.WithTarget(kernelAddress));
    }

    /// <summary>
    /// Replaces __KERNEL_ADDRESS__ placeholder in HTML with the actual kernel address.
    /// Returns the updated HTML string, or the original if no replacement needed.
    /// </summary>
    public static string ReplaceKernelPlaceholder(string html, Address kernelAddress)
    {
        if (html.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder))
            return html.Replace(
                ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
                kernelAddress.ToString());
        return html;
    }
}
