using System.Runtime.CompilerServices;
using MeshWeaver.AI.Threading;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI.Persistence;

/// <summary>
/// Static helper for chat CRUD operations via IPersistenceService.
/// Provides methods for saving, loading, listing, and deleting chats
/// stored in partition folders.
/// </summary>
public static class ChatPersistenceHelper
{
    /// <summary>
    /// Save chat to partition.
    /// </summary>
    /// <param name="persistence">The persistence service.</param>
    /// <param name="partitionPath">The partition path (e.g., "User/{userId}/Chat").</param>
    /// <param name="chat">The chat to save.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SaveChatAsync(
        IPersistenceService persistence,
        string partitionPath,
        Chat chat,
        CancellationToken ct = default)
    {
        // Save the chat as a partition object
        await persistence.SavePartitionObjectsAsync(partitionPath, null, [chat], ct);
    }

    /// <summary>
    /// Load chat by ID from partition.
    /// </summary>
    /// <param name="persistence">The persistence service.</param>
    /// <param name="partitionPath">The partition path.</param>
    /// <param name="chatId">The chat ID to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat if found, null otherwise.</returns>
    public static async Task<Chat?> LoadChatAsync(
        IPersistenceService persistence,
        string partitionPath,
        string chatId,
        CancellationToken ct = default)
    {
        await foreach (var obj in persistence.GetPartitionObjectsAsync(partitionPath).WithCancellation(ct))
        {
            if (obj is Chat chat && chat.Id == chatId)
            {
                return chat;
            }
        }

        return null;
    }

    /// <summary>
    /// List all chats in partition.
    /// </summary>
    /// <param name="persistence">The persistence service.</param>
    /// <param name="partitionPath">The partition path.</param>
    /// <returns>Async enumerable of chats.</returns>
    public static async IAsyncEnumerable<Chat> ListChatsAsync(
        IPersistenceService persistence,
        string partitionPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var obj in persistence.GetPartitionObjectsAsync(partitionPath).WithCancellation(ct))
        {
            if (obj is Chat chat)
            {
                yield return chat;
            }
        }
    }

    /// <summary>
    /// Delete chat from partition.
    /// </summary>
    /// <param name="persistence">The persistence service.</param>
    /// <param name="partitionPath">The partition path.</param>
    /// <param name="chatId">The chat ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task DeleteChatAsync(
        IPersistenceService persistence,
        string partitionPath,
        string chatId,
        CancellationToken ct = default)
    {
        // Load all chats except the one to delete, then save back
        var remainingChats = new List<Chat>();
        await foreach (var obj in persistence.GetPartitionObjectsAsync(partitionPath).WithCancellation(ct))
        {
            if (obj is Chat chat && chat.Id != chatId)
            {
                remainingChats.Add(chat);
            }
        }

        // Delete all and re-save remaining
        await persistence.DeletePartitionObjectsAsync(partitionPath, null, ct);
        if (remainingChats.Count > 0)
        {
            await persistence.SavePartitionObjectsAsync(partitionPath, null, remainingChats.Cast<object>().ToArray(), ct);
        }
    }

    /// <summary>
    /// Get user chat partition path.
    /// Storage location: User/{userId}/Chat
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The partition path for the user's chats.</returns>
    public static string GetUserChatPartition(string userId)
        => $"User/{userId}/Chat";

    /// <summary>
    /// Get node chat partition path.
    /// Storage location: {nodePath}/Chat
    /// </summary>
    /// <param name="nodePath">The node path.</param>
    /// <returns>The partition path for the node's chats.</returns>
    public static string GetNodeChatPartition(string nodePath)
        => $"{nodePath}/Chat";
}
