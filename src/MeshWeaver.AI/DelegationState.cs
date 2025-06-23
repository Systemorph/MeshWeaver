#nullable enable

using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Manages the state for reply_to delegation scenarios.
/// </summary>
public class DelegationState
{
    private DelegationInstruction? _pendingReply;

    /// <summary>
    /// Gets whether there is a pending reply instruction waiting for user input.
    /// </summary>
    public bool HasPendingReply => _pendingReply != null;

    /// <summary>
    /// Gets the pending reply instruction if one exists.
    /// </summary>
    public DelegationInstruction? PendingReply => _pendingReply;

    /// <summary>
    /// Sets a pending reply instruction that will wait for the next user message.
    /// </summary>
    /// <param name="instruction">The reply instruction to set as pending</param>
    public void SetPendingReply(DelegationInstruction instruction)
    {
        if (instruction.Type != DelegationType.ReplyTo)
        {
            throw new ArgumentException("Only ReplyTo instructions can be set as pending", nameof(instruction));
        }

        _pendingReply = instruction;
    }

    /// <summary>
    /// Processes the pending reply with user input and returns the delegation message.
    /// </summary>
    /// <param name="userInput">The user's input message</param>
    /// <returns>The delegation message to send, or null if no pending reply</returns>
    public ChatMessage? ProcessPendingReply(string userInput)
    {
        if (_pendingReply == null)
        {
            return null;
        }

        var delegationMessage = CodeBlockParser.CreateReplyMessage(_pendingReply, userInput);
        var chatMessage = new ChatMessage(ChatRole.User, [new TextContent(delegationMessage)]);

        // Clear the pending reply
        _pendingReply = null;

        return chatMessage;
    }

    /// <summary>
    /// Clears any pending reply instruction.
    /// </summary>
    public void ClearPendingReply()
    {
        _pendingReply = null;
    }
}


/// <summary>
/// Parses code blocks and identifies delegation instructions.
/// </summary>
public static class CodeBlockParser
{
    private static readonly Regex AgentNameRegex = new(@"^(delegate_to|reply_to)\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);    /// <summary>
                                                                                                                                                       /// Processes a code block and determines if it's a delegation instruction or regular code.
                                                                                                                                                       /// </summary>
                                                                                                                                                       /// <param name="codeBlock">The code block to process</param>
                                                                                                                                                       /// <returns>A code block result indicating type and content</returns>
    public static CodeBlockResult ProcessCodeBlock(CodeBlock codeBlock)
    {
        var delegationInstruction = ParseDelegation(codeBlock);
        if (delegationInstruction != null)
        {
            return new CodeBlockResult(CodeBlockType.Delegation, codeBlock, delegationInstruction);
        }

        return new CodeBlockResult(CodeBlockType.Regular, codeBlock, null);
    }

    /// <summary>
    /// Attempts to parse a delegation instruction from a code block.
    /// </summary>
    /// <param name="codeBlock">The code block to parse</param>
    /// <returns>A delegation instruction if found, null otherwise</returns>
    public static DelegationInstruction? ParseDelegation(CodeBlock codeBlock)
    {
        if (codeBlock.Language.Equals("delegate_to", StringComparison.OrdinalIgnoreCase))
        {
            var agentName = ParseAgentName(codeBlock.Header);
            if (!string.IsNullOrEmpty(agentName))
            {
                return new DelegationInstruction(DelegationType.DelegateTo, agentName, codeBlock.Content.Trim());
            }
        }
        else if (codeBlock.Language.Equals("reply_to", StringComparison.OrdinalIgnoreCase))
        {
            var agentName = ParseAgentName(codeBlock.Header);
            if (!string.IsNullOrEmpty(agentName))
            {
                return new DelegationInstruction(DelegationType.ReplyTo, agentName, codeBlock.Content.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the agent name from the header line.
    /// Expected format: delegate_to "AgentName" or reply_to "AgentName"
    /// </summary>
    /// <param name="header">The header line to parse</param>
    /// <returns>The agent name if found, null otherwise</returns>
    private static string? ParseAgentName(string header)
    {
        var match = AgentNameRegex.Match(header);
        if (match.Success && match.Groups.Count >= 3)
        {
            return match.Groups[2].Value;
        }

        return null;
    }

    /// <summary>
    /// Creates a delegation message for the specified agent.
    /// </summary>
    /// <param name="instruction">The delegation instruction</param>
    /// <returns>A formatted message for the agent</returns>
    public static string CreateDelegationMessage(DelegationInstruction instruction)
    {
        return $"@{instruction.AgentName} {instruction.Content}";
    }

    /// <summary>
    /// Creates a reply message that includes user input.
    /// </summary>
    /// <param name="instruction">The reply instruction</param>
    /// <param name="userInput">The user's input to include</param>
    /// <returns>A formatted reply message</returns>
    public static string CreateReplyMessage(DelegationInstruction instruction, string userInput)
    {
        var content = instruction.Content;
        if (!string.IsNullOrWhiteSpace(userInput))
        {
            content += "\n\n<UserInput>\n" + userInput + "\n</UserInput>";
        }

        return $"@{instruction.AgentName} {content}";
    }
}

/// <summary>
/// Represents a delegation instruction parsed from a code block.
/// </summary>
/// <param name="Type">The type of delegation (DelegateTo or ReplyTo)</param>
/// <param name="AgentName">The name of the agent to delegate to</param>
/// <param name="Content">The content/message for the agent</param>
public record DelegationInstruction(DelegationType Type, string AgentName, string Content);

/// <summary>
/// The type of delegation instruction.
/// </summary>
public enum DelegationType
{
    DelegateTo,
    ReplyTo
}

/// <summary>
/// Represents the result of processing a code block.
/// </summary>
/// <param name="Type">The type of code block (Regular or Delegation)</param>
/// <param name="CodeBlock">The original code block</param>
/// <param name="DelegationInstruction">The delegation instruction if this is a delegation block</param>
public record CodeBlockResult(CodeBlockType Type, CodeBlock CodeBlock, DelegationInstruction? DelegationInstruction);

/// <summary>
/// The type of code block.
/// </summary>
public enum CodeBlockType
{
    Regular,
    Delegation
}
