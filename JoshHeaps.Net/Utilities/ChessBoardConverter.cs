using JoshHeaps.Net.Models;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace JoshHeaps.Net.Utilities;

public sealed class ChessBoardConverter
       : JsonConverter<ChessPiece?[,]>
{
    public override ChessPiece?[,] Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        List<ChessPiece?> pieces = [];

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected start of an array, instead received {reader.TokenType}");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                pieces.Add(null);
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"Expected start of an object, instead received {reader.TokenType}");

            var piece = JsonSerializer.Deserialize<ChessPiece>(ref reader, options) ?? throw new JsonException("Deserialized piece was null");
            pieces.Add(piece);

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException($"Expected end of an object, instead received {reader.TokenType}");
        }

        if (reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException($"Expected end of an array, instead received {reader.TokenType}");

        var boardSize = Math.Sqrt(pieces.Count);

        if (boardSize % 1 != 0)
            throw new JsonException($"Expected a square number of pieces, instead received {pieces.Count}");

        ChessPiece?[,] board = new ChessPiece?[(int)boardSize, (int)boardSize];

        foreach (var piece in pieces)
        {
            if (piece is null)
                continue;

            board[piece.Position.Row, piece.Position.Col] = piece;
        }

        return board;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ChessPiece?[,] value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var piece in value)
        {
            if (piece is null)
            {
                writer.WriteNullValue();
                continue;
            }

            JsonSerializer.Serialize(writer, piece);
        }

        writer.WriteEndArray();
    }
}

