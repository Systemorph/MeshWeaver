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
        /// Symbol of the other player
        /// </summary>
        protected abstract char OtherPlayerSymbol { get; }
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

             - X marks {nameof(TicTacToePlayer1)}'s moves, O marks {nameof(TicTacToePlayer2)}'s moves.
             - Empty cells are shown as a subtle dot (·).
              
             When you receive a board state:
             1. Parse the board state from the input: Check for X and O placements.
                If there is no initial board state, start with an empty board. No need to show old board.
                Output the current board state in the following format:
                "Current board:
             
             [board state as ASCII matrix as parsed from previous message]. 
             "
             **EXAMPLE**:             
             "Board after my move:
             
             | X | · | O |
             |---|---|---|
             | · | · | O |
             | · | X | · |
             "
             
             2. Make your move ({PlayerSymbol}) by choosing an empty position.
                Remember you want to win, so you try to create three of your symbols in a row, column, or diagonal.
             3. Output the board **AFTER** your move in the following format:
             "Board after my move:
             
             [board state as ASCII matrix where you place your move into the updated board]. 
             "
             **EXAMPLE**:             
             "Board after my move:
             
             | X | · | O |
             |---|---|---|
             | · | · | O |
             | · | X | · |
             "
             **IMPORTANT**: You must make a move, you cannot forward the board from the previous move.
             4. You win when three of your characters ({PlayerSymbol}) are in a row, in a column or in a diagonal of the table.
             5. Determine if you have won, output the board and say that you won.
             6. If game is not finished, delegate to {OtherPlayer} using the {nameof(ChatPlugin.Delegate)} kernel function of the {nameof(ChatPlugin)}. 
             issuing the following message to agent {OtherPlayer}: "Your turn".
             
             
             **CRITICAL**:
             - Do not output any additional text, only the board state after your move.
             - You must always make a move before delegating to {OtherPlayer}.
             - Do not delegate unless you have made your move and have output the board **AFTER** your move.
             - You must delegate automatically to {OtherPlayer} using the tool, without additional text, until the game is finished.
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
