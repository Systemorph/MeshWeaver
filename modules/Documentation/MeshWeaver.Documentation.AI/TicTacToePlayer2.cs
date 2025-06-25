using MeshWeaver.AI;

namespace MeshWeaver.Documentation.AI;

/// <summary>
/// Base class for TicTacToe players
/// </summary>
public abstract class TicTacToePlayerBase : IAgentDefinition, IAgentWithDelegation
{
    /// <summary>
    /// The name of the agent
    /// </summary>

    public string Name => GetType().Name;
    /// <summary>
    /// name of the other player this agent can delegate to
    /// </summary>
    protected abstract string OtherPlayer { get; }
    /// <summary>
    /// Symbol of this player
    /// </summary>
    protected abstract char PlayerSymbol { get; }
    /// <summary>
    /// Description of what this agent does
    /// </summary>
    public string Description => $"TicTacToe Player ({PlayerSymbol}) - Plays tic-tac-toe and can delegate back to {OtherPlayer} when it's their turn.";
    /// <summary>
    /// Instructions for how this agent should behave
    /// </summary>
    public string Instructions =>
        $"""
        You are {Name} and you play as '{PlayerSymbol}'.

        Game rules:
        - The board is represented as a 3x3 grid with positions 1-9:
        | 1 | 2 | 3 |
        |---|---|---|
        | 4 | 5 | 6 |
        |---|---|---|
        | 7 | 8 | 9 |

        - X marks Player 1's moves, O marks Player 2's moves.
        - Empty cells means that nothing is there.
     
        When you receive a board state:
        1. Parse the board state from the input: Check for X and O placements.
           If there is no initial board state, start with an empty board.
        2. Make your move ({PlayerSymbol}) by choosing an empty position
        4. If game is not finished, delegate to {OtherPlayer} issuing the following message:
        ```delegate_to "{OtherPlayer}"
        [board state as ASCII matrix where you place your move]
        ```
        with the updated board. You must not hand back to user unless game is finished.
        Example:
        ```delegate_to "{OtherPlayer}"
        Current board:
        
        | X |   | O |
        |---|---|---|
        |   |   | O |
        |---|---|---|
        |   | X |   |
        ```
        5. If game is finished, output the final board announce the result and don't delegate
        Example for finished board:
        Game over:
        
        | X |   | O |
        |---|---|---|
        |   |   | O |
        |---|---|---|
        |   | X | O |

        I won.
        """;

    /// <summary>
    /// Gets the delegation descriptions for this agent
    /// </summary>
    public IEnumerable<DelegationDescription> Delegations
    {
        get
        {
            // Always allow delegation to Player 1 - the agent will decide based on game state
            yield return new DelegationDescription(
                $"{OtherPlayer}",
                $"Delegate to {OtherPlayer} when it's their turn and the game is not finished."
            );
        }
    }

}
/// <summary>
/// TicTacToe Player 2 (O) - Can be delegated to by Player 1
/// </summary>
public class TicTacToePlayer2 : TicTacToePlayerBase
{
    /// <inheritdoc />
    protected override string OtherPlayer => nameof(TicTacToePlayer1);

    /// <inheritdoc />
    protected override char PlayerSymbol => 'O';

}
