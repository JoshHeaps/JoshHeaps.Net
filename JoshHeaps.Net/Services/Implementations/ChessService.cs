using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

public class ChessService : IChessService
{
    public GameState CreateNewGame()
    {
        var gameState = new GameState();

        InitializeBoard(gameState);

        return gameState;
    }

    private void InitializeBoard(GameState gameState)
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                gameState.Board[r, c] = null;

        gameState.Pieces.Clear();
        gameState.MoveHistory.Clear();

        gameState.WhiteCanCastleKingside = true;
        gameState.WhiteCanCastleQueenside = true;
        gameState.BlackCanCastleKingside = true;
        gameState.BlackCanCastleQueenside = true;
        gameState.EnPassantTarget = null;

        SetupBlackPieces(gameState);
        SetupWhitePieces(gameState);

        gameState.CurrentPlayer = PieceColor.White;

        UpdateCheckStatus(gameState);
    }

    private static void SetupBlackPieces(GameState gs)
    {
        var blackMajors = new[]
        {
            PieceType.Rook, PieceType.Knight, PieceType.Bishop,
            PieceType.Queen, PieceType.King, PieceType.Bishop,
            PieceType.Knight, PieceType.Rook
        };

        for (int c = 0; c < 8; c++)
        {
            var major = new ChessPiece($"b{blackMajors[c]}{c}", blackMajors[c], PieceColor.Black, new Position(0, c));
            gs.Board[0, c] = major;
            gs.Pieces.Add(major);
        }

        for (int c = 0; c < 8; c++)
        {
            var pawn = new ChessPiece($"bPawn{c}", PieceType.Pawn, PieceColor.Black, new Position(1, c));
            gs.Board[1, c] = pawn;
            gs.Pieces.Add(pawn);
        }
    }

    private static void SetupWhitePieces(GameState gs)
    {
        var whiteMajors = new[]
        {
            PieceType.Rook, PieceType.Knight, PieceType.Bishop,
            PieceType.Queen, PieceType.King, PieceType.Bishop,
            PieceType.Knight, PieceType.Rook
        };

        for (int c = 0; c < 8; c++)
        {
            var major = new ChessPiece($"w{whiteMajors[c]}{c}", whiteMajors[c], PieceColor.White, new Position(7, c));
            gs.Board[7, c] = major;
            gs.Pieces.Add(major);
        }

        for (int c = 0; c < 8; c++)
        {
            var pawn = new ChessPiece($"wPawn{c}", PieceType.Pawn, PieceColor.White, new Position(6, c));
            gs.Board[6, c] = pawn;
            gs.Pieces.Add(pawn);
        }
    }

    public List<(ChessPiece piece, List<Position> moves)> GetAllLegalMoves(GameState gameState)
    {
        var result = new List<(ChessPiece piece, List<Position> moves)>();

        var currentPieces = gameState
            .Pieces
            .Where(p => p.Color == gameState.CurrentPlayer && p.Position.Row >= 0)
            .ToList();

        foreach (var piece in currentPieces)
        {
            var moves = GetLegalMovesForPiece(gameState, piece.Id);

            if (moves.Count > 0)
                result.Add((piece, moves));
        }

        return result;
    }

    public List<Position> GetLegalMovesForPiece(GameState gameState, string pieceId)
    {
        var piece = gameState.Pieces.FirstOrDefault(p => p.Id == pieceId);

        if (piece == null) return [];

        var candidateMoves = GenerateCandidateMoves(gameState, piece);
        var legalMoves = new List<Position>();

        foreach (var pos in candidateMoves)
            if (IsMoveLegalConsideringCheck(gameState, piece, pos))
                legalMoves.Add(pos);

        return legalMoves;
    }

    public MoveResultDto MakeMove(GameState gameState, MoveDto moveDto)
    {
        var piece = gameState.Pieces.FirstOrDefault(p => p.Id == moveDto.PieceId);

        if (piece == null)
            return new MoveResultDto { Success = false, Message = "Piece not found." };

        if (piece.Color != gameState.CurrentPlayer)
            return new MoveResultDto { Success = false, Message = "Not your turn." };

        var legalMoves = GetLegalMovesForPiece(gameState, piece.Id);
        var targetPos = new Position(moveDto.TargetRow, moveDto.TargetCol);

        if (!legalMoves.Any(m => m.Row == moveDto.TargetRow && m.Col == moveDto.TargetCol))
            return new MoveResultDto { Success = false, Message = "Illegal move." };

        PerformMove(gameState, piece, targetPos, moveDto);

        UpdateCheckStatus(gameState);

        var notation = $"{piece.Id}:{piece.Position}->{targetPos}";
        gameState.MoveHistory.Add(notation);

        return new MoveResultDto
        {
            Success = true,
            Message = "Move successful.",
            IsCheck = gameState.IsCheck,
            IsCheckmate = gameState.IsCheckmate,
            IsStalemate = gameState.IsStalemate
        };
    }

    private static void PerformMove(GameState gs, ChessPiece piece, Position targetPos, MoveDto moveDto)
    {
        var oldPos = piece.Position;
        var captured = gs.Board[targetPos.Row, targetPos.Col];

        HandleEnPassantIfNeeded(gs, piece, targetPos, ref captured);

        gs.Board[oldPos.Row, oldPos.Col] = null;
        piece.Position = targetPos;
        gs.Board[targetPos.Row, targetPos.Col] = piece;

        if (captured != null && captured != piece)
            captured.Position = new Position(-1, -1);

        bool wasFirstMove = !piece.HasMoved;
        piece.HasMoved = true;

        HandleCastlingIfNeeded(gs, piece, oldPos, targetPos, wasFirstMove);

        HandlePawnTwoSquareMove(gs, piece, oldPos, targetPos, wasFirstMove);

        HandlePawnPromotionIfNeeded(piece, moveDto);

        UpdateCastlingRights(gs, piece, oldPos);

        gs.CurrentPlayer = gs.CurrentPlayer == PieceColor.White
            ? PieceColor.Black
            : PieceColor.White;
    }

    private static void HandleEnPassantIfNeeded(GameState gs, ChessPiece piece, Position targetPos, ref ChessPiece? captured)
    {
        if (piece.Type != PieceType.Pawn || !gs.EnPassantTarget.HasValue)
            return;

        var enPassantSquare = gs.EnPassantTarget.Value;

        if (targetPos.Row == enPassantSquare.Row && targetPos.Col == enPassantSquare.Col)
        {
            int direction = piece.Color == PieceColor.White ? 1 : -1;
            var capturedPos = new Position(targetPos.Row + direction, targetPos.Col);
            var potentialPawn = gs.Board[capturedPos.Row, capturedPos.Col];

            if (potentialPawn != null && potentialPawn.Color != piece.Color && potentialPawn.Type == PieceType.Pawn)
            {
                gs.Board[capturedPos.Row, capturedPos.Col] = null;
                potentialPawn.Position = new Position(-1, -1);
                captured = potentialPawn;
            }
        }
    }

    private static void HandleCastlingIfNeeded(GameState gs, ChessPiece piece, Position oldPos, Position targetPos, bool wasFirstMove)
    {
        if (piece.Type != PieceType.King || !wasFirstMove)
            return;

        var colDiff = targetPos.Col - oldPos.Col;

        if (Math.Abs(colDiff) == 2)
        {
            bool isKingside = colDiff > 0;
            int rookStartCol = isKingside ? 7 : 0;
            int rookEndCol = isKingside ? 5 : 3;
            var rook = gs.Board[oldPos.Row, rookStartCol];

            if (rook != null && rook.Type == PieceType.Rook && !rook.HasMoved)
            {
                gs.Board[oldPos.Row, rookStartCol] = null;
                rook.Position = new Position(oldPos.Row, rookEndCol);
                gs.Board[oldPos.Row, rookEndCol] = rook;
                rook.HasMoved = true;
            }
        }
    }

    private static void HandlePawnTwoSquareMove(GameState gs, ChessPiece piece, Position oldPos, Position targetPos, bool wasFirstMove)
    {
        gs.EnPassantTarget = null;

        if (piece.Type != PieceType.Pawn || !wasFirstMove)
            return;

        var rowDiff = Math.Abs(targetPos.Row - oldPos.Row);

        if (rowDiff == 2)
        {
            var rowBehind = (oldPos.Row + targetPos.Row) / 2;
            gs.EnPassantTarget = new Position(rowBehind, oldPos.Col);
        }
    }

    private static void HandlePawnPromotionIfNeeded(ChessPiece piece, MoveDto moveDto)
    {
        if (piece.Type != PieceType.Pawn)
            return;

        bool promotionRow = piece.Color == PieceColor.White
            ? piece.Position.Row == 0
            : piece.Position.Row == 7;

        if (!promotionRow) return;

        var promotionChoice = moveDto.PromotionChoice ?? PieceType.Queen;
        piece.Type = promotionChoice;
    }

    private static void UpdateCastlingRights(GameState gs, ChessPiece movedPiece, Position oldPos)
    {
        if (movedPiece.Type == PieceType.King)
        {
            if (movedPiece.Color == PieceColor.White)
            {
                gs.WhiteCanCastleKingside = false;
                gs.WhiteCanCastleQueenside = false;
            }
            else
            {
                gs.BlackCanCastleKingside = false;
                gs.BlackCanCastleQueenside = false;
            }

            return;
        }

        if (movedPiece.Type == PieceType.Rook)
        {
            if (movedPiece.Color == PieceColor.White)
            {
                if (oldPos.Row == 7 && oldPos.Col == 0)
                    gs.WhiteCanCastleQueenside = false;

                if (oldPos.Row == 7 && oldPos.Col == 7)
                    gs.WhiteCanCastleKingside = false;
            }
            else
            {
                if (oldPos.Row == 0 && oldPos.Col == 0)
                    gs.BlackCanCastleQueenside = false;

                if (oldPos.Row == 0 && oldPos.Col == 7)
                    gs.BlackCanCastleKingside = false;
            }
        }
    }

    private IEnumerable<Position> GenerateCandidateMoves(GameState gs, ChessPiece piece)
    {
        return piece.Type switch
        {
            PieceType.Pawn => GeneratePawnMoves(gs, piece),
            PieceType.Rook => GenerateRookMoves(gs, piece),
            PieceType.Knight => GenerateKnightMoves(gs, piece),
            PieceType.Bishop => GenerateBishopMoves(gs, piece),
            PieceType.Queen => GenerateQueenMoves(gs, piece),
            PieceType.King => GenerateKingMoves(gs, piece),
            _ => []
        };
    }

    private bool IsMoveLegalConsideringCheck(GameState gs, ChessPiece piece, Position target)
    {
        var clone = CloneGameState(gs);
        var clonedPiece = clone.Pieces.First(p => p.Id == piece.Id);
        var oldPos = clonedPiece.Position;

        clone.Board[oldPos.Row, oldPos.Col] = null;

        var captured = clone.Board[target.Row, target.Col];

        if (captured != null) captured.Position = new Position(-1, -1);

        clonedPiece.Position = target;
        clone.Board[target.Row, target.Col] = clonedPiece;

        HandleEnPassantOnClone(clone, clonedPiece, target);

        HandleCastlingOnClone(clone, clonedPiece, oldPos, target);

        var myKing = clone
            .Pieces
            .FirstOrDefault(p => p.Color == piece.Color
                                 && p.Type == PieceType.King
                                 && p.Position.Row >= 0);

        if (myKing == null) return false;

        bool inCheck = IsSquareAttacked(clone, myKing.Position, myKing.Color);

        return !inCheck;
    }

    private static void HandleEnPassantOnClone(GameState clone, ChessPiece clonedPiece, Position target)
    {
        if (clonedPiece.Type != PieceType.Pawn || !clone.EnPassantTarget.HasValue)
            return;

        if (target.Row == clone.EnPassantTarget.Value.Row && target.Col == clone.EnPassantTarget.Value.Col)
        {
            int direction = clonedPiece.Color == PieceColor.White ? 1 : -1;
            var capturedPos = new Position(target.Row + direction, target.Col);
            var epCaptured = clone.Board[capturedPos.Row, capturedPos.Col];

            if (epCaptured != null && epCaptured.Color != clonedPiece.Color && epCaptured.Type == PieceType.Pawn)
            {
                clone.Board[capturedPos.Row, capturedPos.Col] = null;
                epCaptured.Position = new Position(-1, -1);
            }
        }
    }

    private static void HandleCastlingOnClone(GameState clone, ChessPiece clonedPiece, Position oldPos, Position target)
    {
        if (clonedPiece.Type != PieceType.King || clonedPiece.HasMoved)
            return;

        int colDiff = target.Col - oldPos.Col;

        if (Math.Abs(colDiff) == 2)
        {
            bool isKingside = colDiff > 0;
            int rookStartCol = isKingside ? 7 : 0;
            int rookEndCol = isKingside ? 5 : 3;

            var rook = clone.Board[oldPos.Row, rookStartCol];

            if (rook != null && rook.Type == PieceType.Rook && !rook.HasMoved)
            {
                clone.Board[oldPos.Row, rookStartCol] = null;
                rook.Position = new Position(oldPos.Row, rookEndCol);
                clone.Board[oldPos.Row, rookEndCol] = rook;
            }
        }
    }

    private bool IsSquareAttacked(GameState gs, Position square, PieceColor colorOfSquare)
    {
        var enemyColor = colorOfSquare == PieceColor.White ? PieceColor.Black : PieceColor.White;

        var enemyPieces = gs
            .Pieces
            .Where(p => p.Color == enemyColor && p.Position.Row >= 0)
            .ToList();

        foreach (var enemy in enemyPieces)
        {
            var moves = GenerateCandidateMoves(gs, enemy);

            if (moves.Any(m => m.Row == square.Row && m.Col == square.Col))
                return true;
        }

        return false;
    }

    private void UpdateCheckStatus(GameState gs)
    {
        gs.IsCheck = false;
        gs.IsCheckmate = false;
        gs.IsStalemate = false;

        var king = gs
            .Pieces
            .FirstOrDefault(p => p.Color == gs.CurrentPlayer
                                 && p.Type == PieceType.King
                                 && p.Position.Row >= 0);

        // If there's no king, that's effectively checkmate for that side.
        if (king == null)
        {
            gs.IsCheck = true;
            gs.IsCheckmate = true;
            return;
        }

        bool inCheck = IsSquareAttacked(gs, king.Position, gs.CurrentPlayer);

        gs.IsCheck = inCheck;

        var allMoves = GetAllLegalMoves(gs);

        if (allMoves.Count == 0 && inCheck)
        {
            gs.IsCheckmate = true;

            return;
        }

        if (allMoves.Count == 0 && !inCheck)
        {
            gs.IsStalemate = true;

            return;
        }
    }

    private static GameState CloneGameState(GameState original)
    {
        var clone = new GameState
        {
            GameId = original.GameId,
            CurrentPlayer = original.CurrentPlayer,
            EnPassantTarget = original.EnPassantTarget,
            WhiteCanCastleKingside = original.WhiteCanCastleKingside,
            WhiteCanCastleQueenside = original.WhiteCanCastleQueenside,
            BlackCanCastleKingside = original.BlackCanCastleKingside,
            BlackCanCastleQueenside = original.BlackCanCastleQueenside,
            MoveHistory = new List<string>(original.MoveHistory),
            Board = new ChessPiece[8,8],
        };

        foreach (var p in original.Pieces)
        {
            var copy = new ChessPiece(p.Id, p.Type, p.Color, p.Position)
            {
                HasMoved = p.HasMoved
            };

            clone.Pieces.Add(copy);
        }

        foreach (var cp in clone.Pieces)
            if (cp.Position.Row >= 0)
                clone.Board[cp.Position.Row, cp.Position.Col] = cp;

        return clone;
    }

    private static List<Position> GeneratePawnMoves(GameState gs, ChessPiece piece)
    {
        var moves = new List<Position>();
        int direction = piece.Color == PieceColor.White ? -1 : 1;
        var startRow = piece.Position.Row;
        var startCol = piece.Position.Col;

        var forward1 = startRow + direction;

        if (IsOnBoard(forward1, startCol) && gs.Board[forward1, startCol] == null)
        {
            moves.Add(new Position(forward1, startCol));

            if (!piece.HasMoved)
            {
                var forward2 = startRow + 2 * direction;

                if (IsOnBoard(forward2, startCol) && gs.Board[forward2, startCol] == null)
                    moves.Add(new Position(forward2, startCol));
            }
        }

        var leftCol = startCol - 1;
        var rightCol = startCol + 1;

        if (IsOnBoard(forward1, leftCol))
        {
            var occupant = gs.Board[forward1, leftCol];

            if (occupant != null && occupant.Color != piece.Color)
                moves.Add(new Position(forward1, leftCol));
        }

        if (IsOnBoard(forward1, rightCol))
        {
            var occupant = gs.Board[forward1, rightCol];

            if (occupant != null && occupant.Color != piece.Color)
                moves.Add(new Position(forward1, rightCol));
        }

        if (gs.EnPassantTarget.HasValue)
        {
            var ep = gs.EnPassantTarget.Value;

            if (ep.Row == forward1 && Math.Abs(ep.Col - startCol) == 1)
            {
                // Verify there's an enemy pawn to capture
                int enemyPawnRow = piece.Color == PieceColor.White ? ep.Row + 1 : ep.Row - 1;
                var enemyPawn = gs.Board[enemyPawnRow, ep.Col];

                if (enemyPawn != null && enemyPawn.Type == PieceType.Pawn && enemyPawn.Color != piece.Color)
                    moves.Add(ep);
            }
        }

        return moves;
    }

    private static List<Position> GenerateRookMoves(GameState gs, ChessPiece piece)
        => GenerateSlidingMoves(gs, piece, [(1, 0), (-1, 0), (0, 1), (0, -1)]);

    private static List<Position> GenerateBishopMoves(GameState gs, ChessPiece piece)
        => GenerateSlidingMoves(gs, piece, [(1, 1), (1, -1), (-1, 1), (-1, -1)]);

    private static List<Position> GenerateQueenMoves(GameState gs, ChessPiece piece)
        => [..GenerateRookMoves(gs, piece), ..GenerateBishopMoves(gs, piece)];

    private static List<Position> GenerateSlidingMoves(GameState gs, ChessPiece piece, (int dr, int dc)[] directions)
    {
        var results = new List<Position>();
        var (startRow, startCol) = piece.Position;

        foreach (var (dr, dc) in directions)
            results.AddRange(GetSlidingMovesInDirection(gs, piece, startRow, startCol, dr, dc));

        return results;
    }

    private static List<Position> GetSlidingMovesInDirection(
        GameState gs,
        ChessPiece piece,
        int row,
        int col,
        int dr,
        int dc)
    {
        var moves = new List<Position>();
        var r = row;
        var c = col;

        while (true)
        {
            r += dr;
            c += dc;

            if (!IsOnBoard(r, c))
                return moves;

            var occupant = gs.Board[r, c];

            if (occupant == null)
            {
                moves.Add(new Position(r, c));
                continue;
            }

            if (occupant.Color != piece.Color)
                moves.Add(new Position(r, c));

            return moves;
        }
    }

    private static IEnumerable<Position> GenerateKnightMoves(GameState gs, ChessPiece piece)
    {
        var offsets = new (int, int)[]
        {
            (2,1), (2,-1), (-2,1), (-2,-1),
            (1,2), (1,-2), (-1,2), (-1,-2)
        };

        foreach (var (dr, dc) in offsets)
        {
            var r = piece.Position.Row + dr;
            var c = piece.Position.Col + dc;

            if (!IsOnBoard(r, c))
                continue;

            var occupant = gs.Board[r, c];

            if (occupant == null || occupant.Color != piece.Color)
                yield return new Position(r, c);
        }
    }

    private List<Position> GenerateKingMoves(GameState gs, ChessPiece piece)
    {
        var results = new List<Position>();
        var offsets = new[]
        {
            (1,0), (-1,0), (0,1), (0,-1),
            (1,1), (1,-1), (-1,1), (-1,-1)
        };

        foreach (var (dr, dc) in offsets)
        {
            int r = piece.Position.Row + dr;
            int c = piece.Position.Col + dc;

            if (!IsOnBoard(r, c))
                continue;

            var occupant = gs.Board[r, c];

            if (occupant == null || occupant.Color != piece.Color)
                results.Add(new Position(r, c));
        }

        if (!piece.HasMoved && !gs.IsCheck && piece.Color == gs.CurrentPlayer)
            AddCastlingMoves(gs, piece, results);

        return results;
    }

    private void AddCastlingMoves(GameState gs, ChessPiece king, List<Position> results)
    {
        bool hasKingsideRight = king.Color == PieceColor.White
            ? gs.WhiteCanCastleKingside
            : gs.BlackCanCastleKingside;

        bool hasQueensideRight = king.Color == PieceColor.White
            ? gs.WhiteCanCastleQueenside
            : gs.BlackCanCastleQueenside;

        var row = king.Position.Row;
        var col = king.Position.Col;

        bool areKingsideCastleSpacesEmpty = IsEmpty(row, col + 1, gs)
            && IsEmpty(row, col + 2, gs);

        bool isKingsideCastleSafe = !IsSquareAttacked(gs, new Position(row, col + 1), king.Color)
            && !IsSquareAttacked(gs, new Position(row, col + 2), king.Color);

        bool areQueensideCastleSpacesEmpty = IsEmpty(row, col - 1, gs)
            && IsEmpty(row, col - 2, gs)
            && IsEmpty(row, col - 3, gs);

        bool isQueensideCastleSafe = !IsSquareAttacked(gs, new Position(row, col - 1), king.Color)
            && !IsSquareAttacked(gs, new Position(row, col - 2), king.Color);

        bool canCastleKingside = areKingsideCastleSpacesEmpty
            && isKingsideCastleSafe
            && hasKingsideRight;

        bool canCastleQueenside = areQueensideCastleSpacesEmpty
            && isQueensideCastleSafe
            && hasQueensideRight;

        if (canCastleKingside) results.Add(new Position(row, col + 2));

        if (canCastleQueenside) results.Add(new Position(row, col - 2));
    }

    private static bool IsEmpty(int r, int c, GameState gs)
    {
        if (!IsOnBoard(r, c)) return false;

        return gs.Board[r, c] == null;
    }

    private static bool IsOnBoard(int r, int c)
        => r >= 0 && r < 8 && c >= 0 && c < 8;
}