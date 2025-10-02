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

        const result = await response.json();

        if (!response.ok || !result.success) {
            throw new Error(result.message || "Invalid move or not your turn.");
        }

        return result;
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
            const moveResult = await this.makeMove(moveDto);

            const updatedGame = await this.getGameState(GameState.currentGameId);
            ChessBoard.renderPieces(updatedGame.pieces);

            GameState.clearSelection();
            ChessInteractions.clearHighlights();

            await ChessSignalR.notifyMoveMade(moveDto, moveResult);
            this.alertGameStatusChange(moveResult);

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
            }

            if (gameOver) {
                await ChessSignalR.leaveGame();
            }
        }, 500);
    }
};

console.log("ChessAPI.js loaded");