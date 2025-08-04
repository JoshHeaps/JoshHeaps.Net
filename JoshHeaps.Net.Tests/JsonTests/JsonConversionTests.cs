using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Utilities;
using System.Text.Json;

namespace JoshHeaps.Net.Tests.JsonTests;

internal class JsonConversionTests
{
    [Test]
    public void ConvertBoard_WhenGivenStartingBoard_CanConvertToAndFromJson()
    {
        // Arrange
        var gameState = new ChessService().CreateNewGame();

        // Act
        var serializedGameState = JsonSerializer.Serialize(gameState);
        var deserializedGameState = JsonSerializer.Deserialize<GameState>(serializedGameState);

        Assert.That(deserializedGameState, Is.Not.Null);

        var originBoard = gameState.Board;
        var convertedBoard = deserializedGameState!.Board;

        // Assert
        AssertBoardEquality(originBoard, convertedBoard);
    }

    [Test]
    public void ConvertBoard_WhenGivenValidBoard_CanConvertToAndFromJson()
    {
        // Arrange
        ChessPiece?[,] originBoard = new ChessPiece?[8,8];
        
        originBoard[0, 0] = new ChessPiece(
            "rook1",
            PieceType.Rook,
            PieceColor.White,
            new Position(0, 0)
        );
        originBoard[4,4] = new ChessPiece(
            "pawn1",
            PieceType.Pawn,
            PieceColor.Black,
            new Position(4, 4)
        );

        // Act
        var serializedBoard = JsonSerializer.Serialize(originBoard, _options);
        var convertedBoard = JsonSerializer.Deserialize<ChessPiece?[,]>(serializedBoard, _options);

        // Assert
        AssertBoardEquality(originBoard, convertedBoard!);
    }

    private static void AssertBoardEquality(ChessPiece?[,]? original, ChessPiece?[,]? converted)
    {
        Assert.Multiple(() =>
        {
            Assert.That(original, Is.Not.Null);
            Assert.That(converted, Is.Not.Null);
        });

        Assert.That(original!.LongLength, Is.EqualTo(converted!.LongLength));

        for (int i = 0; i < original.GetLength(0); i++)
        {
            for (int j = 0; j < original.GetLength(1); j++)
            {
                var originalPiece = original[i, j];
                var convertedPiece = converted[i, j];
                AssertPieceEquality(originalPiece, convertedPiece);
            }
        }
    }

    private static void AssertPieceEquality(ChessPiece? original, ChessPiece? converted)
    {
        if (original is null)
        {
            Assert.That(converted, Is.Null);
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(converted, Is.Not.Null);
            Assert.That(original!.Id, Is.EqualTo(converted!.Id));
            Assert.That(original.Type, Is.EqualTo(converted.Type));
            Assert.That(original.Color, Is.EqualTo(converted.Color));
            Assert.That(original.Position.Row, Is.EqualTo(converted.Position.Row));
            Assert.That(original.Position.Col, Is.EqualTo(converted.Position.Col));
        });
    }

    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new ChessBoardConverter() }
    };
}
