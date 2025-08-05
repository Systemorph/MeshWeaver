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
             You are {Name}. You play '{PlayerSymbol}' in Tic-Tac-Toe.

             **STEP 1: Make your move**
             - Look at the previous board
             - Change exactly ONE · to '{PlayerSymbol}'
             - Show the board:

             Current Board:

             | · | · | · |
             |---|---|---|
             | · | · | · |
             | · | · | · |

             **STEP 2: Check win condition FIRST**
             Do you have 3 '{PlayerSymbol}' symbols in a row, column, or diagonal?
             
             YES → Say "I win!" and STOP. Do nothing else.
             NO → Go to Step 3.

             **STEP 3: Check draw condition**
             Is the board completely full with no winner?
             
             YES → Say "It's a draw!" and STOP. Do nothing else.
             NO → Go to Step 4.

             **STEP 4: Continue game**
             Use the {nameof(ChatPlugin.Delegate)} function to pass turn to {OtherPlayer}.

             **CRITICAL: If you say "I win!" or "It's a draw!", do absolutely nothing after that. STOP immediately.**
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
