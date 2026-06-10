// Material value per piece type, indexed by PieceType (0=Pawn .. 5=King).
const PIECE_VALUES = [1, 5, 3, 3, 9, 0];

const ChessBoard = {
    // Render from a full game-state payload (the shape returned by the move/state
    // endpoints and pushed over SignalR). Ignores state older than what's already
    // shown, so a slow initial fetch can't clobber a move that arrived first.
    renderState(state) {
        if (!state || !GameState.shouldApply(state.version)) return;
        this.renderPieces(state.pieces);
        this.renderCapturedTrays(state.pieces, state.capturedPieces);
        this.renderMoveList(state.sanHistory);
        this.renderStatus(state);
        GameState.setVersion(state.version);
    },

    // Two-column numbered move list (white move, black move), latest highlighted.
    renderMoveList(sanHistory) {
        const list = document.getElementById("moveList");
        if (!list) return;

        const san = sanHistory ?? [];

        if (san.length === 0) {
            list.innerHTML = '<p class="movePlaceholder">Moves will appear here once a game begins.</p>';
            return;
        }

        list.innerHTML = "";

        for (let i = 0; i < san.length; i += 2) {
            const row = document.createElement("div");
            row.className = "moveRow";

            const num = document.createElement("span");
            num.className = "moveNum";
            num.textContent = `${i / 2 + 1}.`;
            row.appendChild(num);

            row.appendChild(this.moveCell(san[i], i === san.length - 1));

            if (i + 1 < san.length)
                row.appendChild(this.moveCell(san[i + 1], i + 1 === san.length - 1));

            list.appendChild(row);
        }

        list.scrollTop = list.scrollHeight;
    },

    moveCell(san, isLatest) {
        const cell = document.createElement("span");
        cell.className = isLatest ? "moveSan latest" : "moveSan";
        cell.textContent = san;
        return cell;
    },

    renderStatus(state) {
        let text;
        let alert = false;

        if (state.isCheckmate) {
            const winner = state.currentPlayer === "White" ? "Black" : "White";
            text = `Checkmate — ${winner} wins`;
            alert = true;
        } else if (state.isStalemate) {
            text = "Draw — stalemate";
            alert = true;
        } else if (state.isThreefoldRepetition) {
            text = "Draw — threefold repetition";
            alert = true;
        } else {
            text = `${state.currentPlayer} to move`;
            if (state.isCheck) {
                text += " — check";
                alert = true;
            }
        }

        // Mirror to both the desktop panel status and the mobile bar status.
        ["statusLine", "mobileStatus"].forEach(id => {
            const el = document.getElementById(id);
            if (!el) return;
            el.textContent = text;
            el.classList.toggle("alert", alert);
        });
    },

    renderPieces(pieces) {
        this.clearAllSquares();
        this.renderCoordinateLabels();
        this.placePieces(pieces);
        this.setupSquareEventHandlers();
        this.highlightPreviousMove();
    },

    // Show each side's captured pieces and the leading side's material advantage,
    // arranged so the current player's tray sits below the board.
    renderCapturedTrays(activePieces, capturedPieces) {
        const captured = capturedPieces ?? [];

        // A captured piece's color is the side that lost it, so White's haul is the
        // captured Black pieces, and vice versa.
        const whiteCaptured = captured.filter(p => p.color === 1);
        const blackCaptured = captured.filter(p => p.color === 0);

        // Net material from pieces still on the board, so promotions count correctly.
        const advantage = (activePieces ?? []).reduce(
            (sum, p) => sum + (p.color === 0 ? PIECE_VALUES[p.type] : -PIECE_VALUES[p.type]), 0);

        const white = { captured: whiteCaptured, advantage: Math.max(advantage, 0) };
        const black = { captured: blackCaptured, advantage: Math.max(-advantage, 0) };

        const isWhite = GameState.currentPlayerIsWhite !== false;
        this.fillCapturedTray("bottom", isWhite ? white : black);
        this.fillCapturedTray("top", isWhite ? black : white);
    },

    fillCapturedTray(position, side) {
        const tray = document.getElementById(`captured-${position}`);
        const badge = document.getElementById(`advantage-${position}`);
        if (!tray || !badge) return;

        tray.innerHTML = "";
        [...side.captured]
            .sort((a, b) => PIECE_VALUES[a.type] - PIECE_VALUES[b.type])
            .forEach(piece => {
                const img = document.createElement("img");
                img.src = ChessUtils.getPieceImageUrl(piece);
                img.alt = piece.type;
                img.className = "capturedPiece";
                img.draggable = false;
                tray.appendChild(img);
            });

        badge.textContent = side.advantage > 0 ? `+${side.advantage}` : "";
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
    },

    renderCoordinateLabels() {
        const files = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'];
        const ranks = ['8', '7', '6', '5', '4', '3', '2', '1'];

        // If player is black, reverse the coordinates
        if (!GameState.currentPlayerIsWhite) {
            files.reverse();
            ranks.reverse();
        }

        for (let i = 0; i < 64; i++) {
            const square = document.getElementById(`square-${i}`);
            const row = Math.floor(i / 8);
            const col = i % 8;

            // Add rank label (1-8) on the leftmost column
            if (col === 0) {
                const rankLabel = document.createElement('span');
                rankLabel.className = 'coordinate-label row-label';
                rankLabel.textContent = ranks[row];
                square.appendChild(rankLabel);
            }

            // Add file label (a-h) on the bottom row
            if (row === 7) {
                const fileLabel = document.createElement('span');
                fileLabel.className = 'coordinate-label col-label';
                fileLabel.textContent = files[col];
                square.appendChild(fileLabel);
            }
        }
    }
};

console.log("ChessBoard.js loaded");