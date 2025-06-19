#nullable enable

using System.Text;

namespace MeshWeaver.AI;

/// <summary>
/// Tracks markdown code blocks during streaming, buffering content until complete blocks are detected.
/// </summary>
public class CodeBlockTracker
{
    private readonly StringBuilder _buffer = new();
    private CodeBlockState _state = CodeBlockState.Normal;
    private int _openingFenceLength = 0;
    private int _currentFenceLength = 0;
    private string _language = string.Empty;
    private string _header = string.Empty;
    private readonly StringBuilder _codeContent = new();

    public bool IsInCodeBlock => _state != CodeBlockState.Normal;
    public bool HasCompleteBlock { get; private set; }
    public CodeBlock? CompletedBlock { get; private set; }

    /// <summary>
    /// Processes a character and returns any content that can be immediately streamed.
    /// </summary>
    /// <param name="c">The character to process</param>
    /// <returns>Content that can be streamed immediately, or null if buffering</returns>
    public string? ProcessCharacter(char c)
    {
        _buffer.Append(c);

        switch (_state)
        {
            case CodeBlockState.Normal:
                return ProcessNormalState(c);

            case CodeBlockState.PotentialFence:
                return ProcessPotentialFenceState(c);

            case CodeBlockState.InHeader:
                return ProcessInHeaderState(c);

            case CodeBlockState.InCode:
                return ProcessInCodeState(c);

            case CodeBlockState.PotentialClosingFence:
                return ProcessPotentialClosingFenceState(c);

            default:
                return null;
        }
    }    /// <summary>
         /// Processes any remaining buffered content when the stream ends.
         /// </summary>
         /// <returns>Any buffered content that should be output</returns>
    public string? Flush()
    {
        // Handle case where we're in a potential closing fence at end of stream
        if (_state == CodeBlockState.PotentialClosingFence && _currentFenceLength >= _openingFenceLength)
        {
            // Complete the block since we have enough backticks to close it
            CompleteBlock();
            return null;
        }

        if (_buffer.Length > 0)
        {
            var content = _buffer.ToString();
            Reset();
            return content;
        }
        return null;
    }/// <summary>
     /// Resets the tracker state.
     /// </summary>
    public void Reset()
    {
        _buffer.Clear();
        _state = CodeBlockState.Normal;
        _openingFenceLength = 0;
        _currentFenceLength = 0;
        _language = string.Empty;
        _header = string.Empty;
        _codeContent.Clear();
        HasCompleteBlock = false;
        CompletedBlock = null;
    }
    private string? ProcessNormalState(char c)
    {
        if (c == '`')
        {
            _state = CodeBlockState.PotentialFence;
            _currentFenceLength = 1;
            return null; // Start buffering
        }

        // Normal character - return immediately and reset buffer
        var result = _buffer.ToString();
        _buffer.Clear();
        return result;
    }

    private string? ProcessPotentialFenceState(char c)
    {
        if (c == '`')
        {
            _currentFenceLength++;
            return null; // Continue buffering
        }

        if (_currentFenceLength >= 3)
        {
            // We have a valid fence, store the opening fence length and start processing header
            _openingFenceLength = _currentFenceLength;
            _state = CodeBlockState.InHeader;
            return ProcessInHeaderState(c);
        }

        // Not enough backticks for a fence - flush buffer and return to normal
        var result = _buffer.ToString();
        _buffer.Clear();
        _state = CodeBlockState.Normal;
        return result;
    }

    private string? ProcessInHeaderState(char c)
    {
        if (c == '\n' || c == '\r')
        {
            // End of header line
            ParseHeader();
            _state = CodeBlockState.InCode;
            _codeContent.Clear();
            return null; // Continue buffering
        }

        _header += c;
        return null; // Continue buffering
    }
    private string? ProcessInCodeState(char c)
    {
        if (c == '`')
        {
            _state = CodeBlockState.PotentialClosingFence;
            _currentFenceLength = 1;
            return null; // Start checking for closing fence
        }

        _codeContent.Append(c);
        return null; // Continue buffering
    }

    private string? ProcessPotentialClosingFenceState(char c)
    {
        if (c == '`')
        {
            _currentFenceLength++;
            if (_currentFenceLength == _openingFenceLength)
            {
                // We have a closing fence - complete the block
                CompleteBlock();
                return null; // Block completed, don't output anything
            }
            return null; // Continue checking
        }


        // Not enough backticks - add them to code content and continue
        for (int i = 0; i < _currentFenceLength; i++)
        {
            _codeContent.Append('`');
        }
        _codeContent.Append(c);
        _state = CodeBlockState.InCode;
        return null;
    }

    private void ParseHeader()
    {
        var header = _header.Trim();
        if (string.IsNullOrEmpty(header))
        {
            _language = string.Empty;
            return;
        }

        // Extract the first word as the language
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _language = parts.Length > 0 ? parts[0] : string.Empty;
    }

    private void CompleteBlock()
    {
        CompletedBlock = new CodeBlock(
            _language,
            _header.Trim(),
            _codeContent.ToString()
        );

        HasCompleteBlock = true;
    }
}

public enum CodeBlockState
{
    Normal,
    PotentialFence,
    InHeader,
    InCode,
    PotentialClosingFence
}

/// <summary>
/// Represents a completed markdown code block.
/// </summary>
/// <param name="Language">The language identifier (first word after opening fence)</param>
/// <param name="Header">The full header line after the opening fence</param>
/// <param name="Content">The code content inside the block</param>
public record CodeBlock(string Language, string Header, string Content);
