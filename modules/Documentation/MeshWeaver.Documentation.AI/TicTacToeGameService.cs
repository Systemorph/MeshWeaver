namespace MeshWeaver.Documentation.AI;

/// <summary>
/// Singleton service to manage the shared TicTacToe game state
/// </summary>
public class TicTacToeGameService
{
    private static readonly TicTacToeGameState _gameState = new();

    public TicTacToeGameState GameState => _gameState;

    /// <summary>
    /// Resets the game to initial state
    /// </summary>
    public void ResetGame()
    {
        _gameState.Board = new char[3, 3];
        _gameState.CurrentPlayer = 'X';
        _gameState.IsGameFinished = false;
        _gameState.Winner = null;

        // Initialize empty board
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                _gameState.Board[i, j] = ' ';
            }
        }
    }
}
