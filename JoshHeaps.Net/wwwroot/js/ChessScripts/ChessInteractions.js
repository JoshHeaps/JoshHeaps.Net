const ChessInteractions = {
    async handlePieceClick(piece) {
        this.clearHighlights();
        GameState.setSelectedPiece(piece);

        try {
            const moves = await ChessAPI.getLegalMoves(piece.id);
            GameState.setLegalMoves(moves);
            this.highlightSelected(piece.row, piece.col);
            this.highlightLegalMoves(moves);
        } catch (err) {
            console.error("Error in handlePieceClick:", err);
        }
    },

    highlightSelected(row, col) {
        const [r, c] = ChessUtils.flipCoordinates(row, col);
        const index = r * 8 + c;
        document.getElementById(`square-${index}`).classList.add("selected");
    },

    highlightLegalMoves(moves) {
        moves.forEach(move => {
            const [r, c] = ChessUtils.flipCoordinates(move.row, move.col);
            const index = r * 8 + c;
            const square = document.getElementById(`square-${index}`);
            square.classList.add("legal");

            const img = square.querySelector("img");
            if (img) img.onclick = null;

            let overlay = document.createElement("div");
            overlay.className = "legalOverlay";
            square.appendChild(overlay);

            square.onclick = () => ChessAPI.handleMove(r, c);
        });
    },

    clearHighlights() {
        for (let i = 0; i < 64; i++) {
            const square = document.getElementById(`square-${i}`);

            const overlay = square.querySelector(".legalOverlay");
            if (overlay) square.removeChild(overlay);

            if (square.classList.contains("legal")) {
                square.classList.remove("legal");
                square.onclick = null;
            }

            square.classList.remove("selected");
        }

        GameState.clearSelection();
    }
};

console.log("ChessInteractions.js loaded");