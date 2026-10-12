#nullable enable

using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Request for autocomplete suggestions based on query and context.
/// </summary>
/// <param name="Query">The search query (text being typed, including any @ or / prefix).</param>
/// <param name="Context">The current unified reference context (e.g., "pricing/MS-2024").</param>
/// <remarks>
/// 🚨 <b>Read on the target is required</b> (MeshWeaver#6570). The answer is assembled from the
/// target node's own providers — its data entity ids, its content files, its indexed chunks — so it
/// is a read of that node, and the access gate decides it exactly as it decides a
/// <c>GetDataRequest</c>. Without the attribute any caller could list another node's files by
/// typing <c>@</c> with that node as the context.
/// </remarks>
[RequiresPermission(Permission.Read)]
public record AutocompleteRequest(string Query, string? Context) : IRequest<AutocompleteResponse>;
