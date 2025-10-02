const ChessBoard = {
    renderPieces(pieces) {
        this.clearAllSquares();
        this.placePieces(pieces);
        this.setupSquareEventHandlers();
        this.highlightPreviousMove();
    },

    clearAllSquares() {
        for (let i = 0; i < 64; i++) {
            const square = document.getElementById(`square-${i}`);
            square.innerHTML = "";
        }
    },

    placePieces(pieces) {
        pieces.forEach(piece => {
            const [r, c] = ChessUtils.flipCoordinates(piece.row, piece.col);
            const index = r * 8 + c;
            const square = document.getElementById(`square-${index}`);
            if (!square) return;

            const img = document.createElement("img");
            img.src = ChessUtils.getPieceImageUrl(piece);
            img.alt = piece.type;
            img.classList.add("chessPiece");

            this.setupPieceDragEvents(img, piece);
            this.setupPieceClickEvent(img, piece);

            square.appendChild(img);
        });
    },

    setupPieceDragEvents(img, piece) {
        img.draggable = true;
        img.ondragstart = async (e) => {
            GameState.setSelectedPiece(piece);
            try {
                const moves = await ChessAPI.getLegalMoves(piece.id);
                GameState.setLegalMoves(moves);
                ChessInteractions.highlightSelected(piece.row, piece.col);
                ChessInteractions.highlightLegalMoves(moves);
            } catch (err) {
                console.error(err);
            }

            e.dataTransfer.setData("text/plain", JSON.stringify({
                srcRow: piece.Row,
                srcCol: piece.Col
            }));
        };

        img.ondragend = () => ChessInteractions.clearHighlights();
    },

    setupPieceClickEvent(img, piece) {
        img.onclick = (e) => {
            e.stopPropagation();
            ChessInteractions.handlePieceClick(piece);
            console.log("Clicked on:", piece);
        };
    },

    setupSquareEventHandlers() {
        for (let i = 0; i < 64; i++) {
            const square = document.getElementById(`square-${i}`);
            square.classList.remove("previous-start", "previous-end");

            if (!square.classList.contains("legal")) {
                square.onclick = () => ChessInteractions.clearHighlights();
            }
        }
    },

    highlightPreviousMove() {
        if (GameState.previousMoveStart && GameState.previousMoveEnd) {
            const [startRow, startCol] = ChessUtils.flipCoordinates(...GameState.previousMoveStart);
            const [endRow, endCol] = ChessUtils.flipCoordinates(...GameState.previousMoveEnd);

            const startIndex = startRow * 8 + startCol;
            const endIndex = endRow * 8 + endCol;

            document.getElementById(`square-${startIndex}`)?.classList.add("previous-start");
            document.getElementById(`square-${endIndex}`)?.classList.add("previous-end");
        }
    },

    setupDragAndDrop() {
        for (let i = 0; i < 64; i++) {
            const square = document.getElementById(`square-${i}`);

            square.ondragover = (e) => {
                e.preventDefault();
            };

            square.ondrop = (e) => {
                e.preventDefault();

                if (!GameState.selectedPiece) return;

                const index = parseInt(square.id.split('-')[1], 10);
                const targetRow = Math.floor(index / 8);
                const targetCol = index % 8;

                ChessAPI.handleMove(targetRow, targetCol);
            };
        }
    }
};

console.log("ChessBoard.js loaded");