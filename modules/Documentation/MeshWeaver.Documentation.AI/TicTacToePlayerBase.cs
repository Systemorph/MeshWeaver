using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;

namespace MeshWeaver.Documentation.AI
{
    /// <summary>
    /// Base class for TicTacToe players
    /// </summary>
    public abstract class TicTacToePlayerBase : IAgentWithDelegations
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
             | · | · | · |
             |---|---|---|
             | · | · | · |
             | · | · | · |

             - X marks Player 1's moves, O marks Player 2's moves.
             - Empty cells are shown as a subtle dot (·).
              
             When you receive a board state:
             1. Parse the board state from the input: Check for X and O placements.
                If there is no initial board state, start with an empty board. No need to show old board.
             2. Make your move ({PlayerSymbol}) by choosing an empty position. 
             **DO NOT output any text or the board**, it will be handled by the delegation. Minimize your
             answer to save output tokens.
             3. You win when three of your characters ({PlayerSymbol}) are in a row, in a column or in a diagonal of the table.
             4. Remember you want to win, so you try to create three of your symbols in a row, column, or diagonal.
             5. Determine if you have won, output the board and say that you won.
             6. If game is not finished, delegate to {OtherPlayer} using the {nameof(ChatPlugin.Delegate)} kernel function of the {nameof(ChatPlugin)}. 
             issuing the following message to agent {OtherPlayer}:
             "Current board:
             
             [board state as ASCII matrix where you place your move]with the updated board]. 
             "
             **EXAMPLE**:             
             "Current Board:
             
             | X | · | O |
             |---|---|---|
             | · | · | O |
             | · | X | · |
             "
             **IMPORTANT**: You must not return the board state to the user, 
             only delegate to {OtherPlayer}.
             
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
}
