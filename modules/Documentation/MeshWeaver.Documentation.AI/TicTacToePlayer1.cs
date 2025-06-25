using MeshWeaver.AI;

namespace MeshWeaver.Documentation.AI;

/// <summary>
/// TicTacToe Player 1 (X) - Exposed in navigator for delegation
/// </summary>
[ExposedInNavigator]
public class TicTacToePlayer1 : TicTacToePlayerBase
{
    /// <inheritdoc />
    protected override string OtherPlayer => nameof(TicTacToePlayer2);

    /// <inheritdoc />
    protected override char PlayerSymbol => 'X';
}
