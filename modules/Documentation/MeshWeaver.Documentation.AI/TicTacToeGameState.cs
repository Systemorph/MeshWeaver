namespace MeshWeaver.Documentation.AI;

/// <summary>
/// Represents the state of a Tic-Tac-Toe game
/// </summary>
public class TicTacToeGameState
{
    /// <summary>
    /// The game board represented as a 3x3 array
    /// </summary>
    public char[,] Board { get; set; } = new char[3, 3];

    /// <summary>
    /// The current player ('X' or 'O')
    /// </summary>
    public char CurrentPlayer { get; set; } = 'X';

    /// <summary>
    /// Whether the game has finished (win or draw)
    /// </summary>
    public bool IsGameFinished { get; set; } = false;

    /// <summary>
    /// The winner of the game ('X', 'O', or 'Draw'), or null if game is ongoing
    /// </summary>
    public string? Winner { get; set; }

    /// <summary>
    /// Initializes a new TicTacToe game with an empty board
    /// </summary>
    public TicTacToeGameState()
    {
        // Initialize empty board
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Board[i, j] = ' ';
            }
        }
    }

    /// <summary>
    /// Makes a move at the specified position
    /// </summary>
    public bool MakeMove(int row, int col)
    {
        if (row < 0 || row >= 3 || col < 0 || col >= 3 || Board[row, col] != ' ' || IsGameFinished)
            return false;

        Board[row, col] = CurrentPlayer;
        CheckGameEnd();

        if (!IsGameFinished)
        {
            CurrentPlayer = CurrentPlayer == 'X' ? 'O' : 'X';
        }

        return true;
    }

    /// <summary>
    /// Checks if the game has ended (win or draw)
    /// </summary>
    private void CheckGameEnd()
    {
        // Check rows
        for (int i = 0; i < 3; i++)
        {
            if (Board[i, 0] != ' ' && Board[i, 0] == Board[i, 1] && Board[i, 1] == Board[i, 2])
            {
                Winner = Board[i, 0].ToString();
                IsGameFinished = true;
                return;
            }
        }

        // Check columns
        for (int j = 0; j < 3; j++)
        {
            if (Board[0, j] != ' ' && Board[0, j] == Board[1, j] && Board[1, j] == Board[2, j])
            {
                Winner = Board[0, j].ToString();
                IsGameFinished = true;
                return;
            }
        }

        // Check diagonals
        if (Board[0, 0] != ' ' && Board[0, 0] == Board[1, 1] && Board[1, 1] == Board[2, 2])
        {
            Winner = Board[0, 0].ToString();
            IsGameFinished = true;
            return;
        }

        if (Board[0, 2] != ' ' && Board[0, 2] == Board[1, 1] && Board[1, 1] == Board[2, 0])
        {
            Winner = Board[0, 2].ToString();
            IsGameFinished = true;
            return;
        }

        // Check for draw
        bool boardFull = true;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (Board[i, j] == ' ')
                {
                    boardFull = false;
                    break;
                }
            }
            if (!boardFull) break;
        }

        if (boardFull)
        {
            Winner = "Draw";
            IsGameFinished = true;
        }
    }

    /// <summary>
    /// Gets the board as a markdown table
    /// </summary>
    public string GetBoardAsMarkdown()
    {
        var result = "| | | |\n|---|---|---|\n";
        for (int i = 0; i < 3; i++)
        {
            result += "|";
            for (int j = 0; j < 3; j++)
            {
                var cell = Board[i, j] == ' ' ? $"{i * 3 + j + 1}" : Board[i, j].ToString();
                result += $" {cell} |";
            }
            result += "\n";
        }
        return result;
    }
}
