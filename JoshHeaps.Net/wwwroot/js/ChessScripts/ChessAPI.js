const ChessAPI = {
    async joinGame() {
        const response = await fetch('/api/chess/JoinGame');
        return await response.json();
    },

    async createCPUGame(difficulty) {
        const response = await fetch(`/api/chess/new/${difficulty}`);
        return await response.json();
    },

    async getGameState(gameId) {
        const response = await fetch(`/api/chess/${gameId}`);
        return await response.json();
    },

    async getPgn(gameId) {
        const response = await fetch(`/api/chess/${gameId}/pgn`);
        if (!response.ok) throw new Error("PGN unavailable");
        return await response.text();
    },

    async forfeit(gameId, playerId) {
        await fetch("/api/chess/forfeit", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ GameId: gameId, PlayerId: playerId })
        });
    },

    async getLegalMoves(pieceId) {
        const response = await fetch(`/api/chess/${GameState.currentGameId}/legalMoves/${pieceId}`);
        if (!response.ok) throw new Error("API failed");
        return await response.json();
    },

    async makeMove(moveDto) {
        const response = await fetch("/api/chess/move", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(moveDto)
        });

        if (!response.ok) {
            let message = await response.text();
            throw new Error(message || "Invalid move or not your turn.");
        }

        // The move endpoint returns both the move result and the full resulting
        // board state, so the caller can render without a follow-up fetch.
        const data = await response.json();

        if (!data.result.success) {
            throw new Error(data.result.message || "Invalid move or not your turn.");
        }

        return data;
    },

    async handleMove(targetRow, targetCol) {
        if (!GameState.selectedPiece) return;

        const isPawn = GameState.selectedPiece.type === 0;
        const reachedEnd = (Number(GameState.selectedPiece.color) === 0 && Number(targetRow) === 0) ||
            (Number(GameState.selectedPiece.color) === 1 && Number(targetRow) === 0);

        let choice = null;

        if (isPawn && reachedEnd) {
            choice = await ChessModals.promptPromotion();
            if (!choice) return;
        }

        [targetRow, targetCol] = ChessUtils.flipCoordinates(targetRow, targetCol);

        const moveDto = {
            GameId: GameState.currentGameId,
            PlayerId: GameState.currentPlayerId,
            PieceId: GameState.selectedPiece.id,
            SourceRow: GameState.selectedPiece.row,
            SourceCol: GameState.selectedPiece.col,
            TargetRow: targetRow,
            TargetCol: targetCol,
            PromotionChoice: choice
        };

        GameState.setPreviousMove([GameState.selectedPiece.row, GameState.selectedPiece.col], [targetRow, targetCol]);

        try {
            const { result, state } = await this.makeMove(moveDto);

            ChessBoard.renderState(state);

            GameState.clearSelection();
            ChessInteractions.clearHighlights();

            this.alertGameStatusChange(result);

        } catch (error) {
            alert("❌ " + error.message);
        }
    },

    alertGameStatusChange(moveResult) {
        setTimeout(async () => {
            let gameOver = false;

            if (moveResult.isCheck) {
                console.log("🛑 Check!");
            }

            if (moveResult.isCheckmate) {
                alert("♟️ Checkmate!");
                gameOver = true;
            } else if (moveResult.isStalemate) {
                alert("🤝 Stalemate!");
                gameOver = true;
            } else if (moveResult.isThreefoldRepetition) {
                alert("🤝 Draw by threefold repetition!");
                gameOver = true;
            }

            if (gameOver) {
                showCopyPgn(GameState.currentGameId);
                await ChessSignalR.leaveGame();
            }
        }, 500);
    }
};

console.log("ChessAPI.js loaded");