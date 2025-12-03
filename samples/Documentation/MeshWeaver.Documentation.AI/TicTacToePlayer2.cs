namespace MeshWeaver.Documentation.AI;

/// <summary>
/// TicTacToe Player 2 (O) - Can be delegated to by Player 1
/// </summary>
public class TicTacToePlayer2 : TicTacToePlayerBase
{
    /// <inheritdoc />
    protected override string OtherPlayer => nameof(TicTacToePlayer1);

    /// <inheritdoc />
    protected override char PlayerSymbol => 'O';

    /// <inheritdoc />
    protected override char OtherPlayerSymbol => 'X';
}
