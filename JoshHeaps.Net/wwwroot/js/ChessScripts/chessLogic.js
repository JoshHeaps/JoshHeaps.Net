let currentGameId = null;
let currentPlayerId = null;
let currentPlayerIsWhite = null;
let signalRConnection = null;
let selectedPiece = null;
let legalMoves = [];
let previousMoveStart = null;
let previousMoveEnd = null;

async function startNewGame() {
    // Stop previous SignalR connection if needed
    if (signalRConnection) {
        await signalRConnection.stop();
        signalRConnection = null;
    }

    // Join the game via API
    const response = await fetch('/api/chess/JoinGame');
    const gameData = await response.json();

    currentGameId = gameData.gameId;
    currentPlayerId = gameData.id;
    currentPlayerIsWhite = gameData.isWhite;
    previousMoveStart = null;
    previousMoveEnd = null;
    document.cookie = `chessGameId=${currentGameId}; path=/; max-age=86400`; // expires in 1 day
    document.cookie = `chessPlayerId=${currentPlayerId}; path=/; max-age=86400`;
    document.cookie = `chessPlayerIsWhite=${currentPlayerIsWhite}; path=/; max-age=86400`
    console.log("🆕 Game started:", currentGameId);

    // Build and start SignalR connection
    await setupSignalRConnection();

    // Render initial state
    const gameState = await fetch(`/api/chess/${currentGameId}`);
    const data = await gameState.json();
    
    renderPieces(data.pieces);
}

function renderPieces(pieces) {
    // Clear all squares
    for (let i = 0; i < 64; i++) {
        const square = document.getElementById(`square-${i}`);
        square.innerHTML = ""; // Remove any previous images
    }

    // Place each piece
    pieces.forEach(piece => {
        const [r, c] = flipCoordinates(piece.row, piece.col);

        const index = r * 8 + c;
        const square = document.getElementById(`square-${index}`);
        if (!square) return;

        const img = document.createElement("img");
        img.src = getPieceImageUrl(piece);
        img.alt = piece.type;
        img.classList.add("chessPiece");

        img.onclick = (e) => {
            e.stopPropagation(); // 👈 Prevents the parent square click from firing
            handlePieceClick(piece);
            console.log("Clicked on:", piece);
        };

        square.appendChild(img);
    });

    for (let i = 0; i < 64; i++) {
        const square = document.getElementById(`square-${i}`);
        square.classList.remove("previous-start", "previous-end");

        if (!square.classList.contains("legal")) {
            square.onclick = () => clearHighlights();
        }
    }

    if (previousMoveStart && previousMoveEnd) {
        const [startRow, startCol] = flipCoordinates(...previousMoveStart);
        const [endRow, endCol] = flipCoordinates(...previousMoveEnd);

        const startIndex = startRow * 8 + startCol;
        const endIndex = endRow * 8 + endCol;

        document.getElementById(`square-${startIndex}`)?.classList.add("previous-start");
        document.getElementById(`square-${endIndex}`)?.classList.add("previous-end");
    }
}

function getPieceImageUrl(piece) {
    const basePath = "/images/Chess Images/";
    const color = piece.color === 0 ? "White" : "Black"; // Or use "White"/"Black" if string
    const typeMap = {
        0: "Pawn",
        1: "Rook",
        2: "Knight",
        3: "Bishop",
        4: "Queen",
        5: "King"
    };

    return basePath + color + typeMap[piece.type] + ".svg";
}

async function handlePieceClick(piece) {
    clearHighlights();

    selectedPiece = piece;

    try {
        const res = await fetch(`/api/chess/${currentGameId}/legalMoves/${piece.id}`);
        if (!res.ok) throw new Error("API failed");

        legalMoves = await res.json();
        highlightSelected(piece.row, piece.col);
        highlightLegalMoves(legalMoves);
    } catch (err) {
        console.error("Error in handlePieceClick:", err);
    }
}

function highlightSelected(row, col) {
    const [r, c] = flipCoordinates(row, col);
    const index = r * 8 + c;
    document.getElementById(`square-${index}`).classList.add("selected");
}

function highlightLegalMoves(moves) {
    moves.forEach(move => {
        const [r, c] = flipCoordinates(move.row, move.col);
        const index = r * 8 + c;
        const square = document.getElementById(`square-${index}`);
        square.classList.add("legal");

        // Remove click from piece (if present) so square click handles it
        const img = square.querySelector("img");
        if (img) img.onclick = null;

        // Let the square itself handle the move
        square.onclick = () => handleMove(r, c);
    });
}

async function handleMove(targetRow, targetCol) {
    if (!selectedPiece) return;

    [targetRow, targetCol] = flipCoordinates(targetRow, targetCol);

    const moveDto = {
        GameId: currentGameId,
        PlayerId: currentPlayerId,
        PieceId: selectedPiece.id,
        SourceRow: selectedPiece.row,
        SourceCol: selectedPiece.col,
        TargetRow: targetRow,
        TargetCol: targetCol,
        PromotionChoice: null // optional
    };

    previousMoveStart = [selectedPiece.row, selectedPiece.col];
    previousMoveEnd = [targetRow, targetCol];

    const res = await fetch("/api/chess/move", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(moveDto)
    });

    const moveResultDto = await res.json();

    if (!res.ok || !moveResultDto.success) {
        alert("❌ " + (moveResultDto.message || "Invalid move or not your turn."));
        return;
    }

    const updatedGame = await fetch(`/api/chess/${currentGameId}`).then(r => r.json());
    renderPieces(updatedGame.pieces);

    // Reset state
    selectedPiece = null;
    legalMoves = [];
    clearHighlights();
    await signalRConnection.invoke("MoveMade", currentGameId, moveDto, moveResultDto);

    alertGameStatusChange(moveResultDto);
}

function clearHighlights() {
    for (let i = 0; i < 64; i++) {
        const square = document.getElementById(`square-${i}`);

        // Remove legal move markers and their onclicks
        if (square.classList.contains("legal")) {
            square.classList.remove("legal");
            square.onclick = null;
        }

        // Always remove selection styling
        square.classList.remove("selected");
    }

    selectedPiece = null;
    legalMoves = [];
}

function alertGameStatusChange(moveResultDto) {
    setTimeout(async () => {
        let gameOver = false;
        // 🟡 Optional: show check
        if (moveResultDto.isCheck) {
            console.log("🛑 Check!");
        }

        // ✅ Handle game end
        if (moveResultDto.isCheckmate) {
            alert("♟️ Checkmate!");
            gameOver = true;
        } else if (moveResultDto.isStalemate) {
            alert("🤝 Stalemate!");
            gameOver = true;
        }

        if (gameOver) {
            await signalRConnection.invoke("LeaveWebsocketGroup", currentGameId);
            await signalRConnection.stop();
            signalRConnection = null;
        }
    }, 500);
}

function flipCoordinates(row, col) {
    if (!currentPlayerIsWhite) {
        return [7 - row, 7 - col];
    }
    return [row, col];
}

function getCookie(name) {
    const value = document.cookie.split('; ')
        .find(row => row.startsWith(name + '='));
    return value ? value.split('=')[1] : null;
}

async function setupSignalRConnection() {
    signalRConnection = new signalR.HubConnectionBuilder()
        .withUrl("/chessHub")
        .configureLogging(signalR.LogLevel.Information)
        .build();

    signalRConnection.onclose(err => {
        console.error("❌ SignalR connection closed:", err?.message);
    });

    signalRConnection.on("ReceiveMoveUpdate", async (gameId, moveDto, moveResultDto) => {
        if (gameId !== currentGameId) return;

        const res = await fetch(`/api/chess/${gameId}`);
        const data = await res.json();
        previousMoveStart = [moveDto.sourceRow, moveDto.sourceCol];
        previousMoveEnd = [moveDto.targetRow, moveDto.targetCol];
        renderPieces(data.pieces);

        alertGameStatusChange(moveResultDto);
    });

    try {
        await signalRConnection.start();
        console.log("✅ SignalR connected");
        await signalRConnection.invoke("JoinWebsocketGroup", currentGameId);
    } catch (err) {
        console.error("❌ SignalR failed to start or join:", err);
    }
}

console.log("chessLogic.js loaded");
window.startNewGame = startNewGame;

window.addEventListener('load', async () => {
    const savedGameId = getCookie("chessGameId");
    const savedPlayerId = getCookie("chessPlayerId");
    const savedPlayerIsWhite = getCookie("chessPlayerIsWhite");

    if (savedGameId && savedPlayerId) {
        try {
            const response = await fetch(`/api/chess/${savedGameId}`);
            if (response.ok) {
                console.log("🧠 Rejoining saved game...");
                currentGameId = savedGameId;
                currentPlayerId = savedPlayerId;
                currentPlayerIsWhite = (savedPlayerIsWhite === "true");

                await setupSignalRConnection(); // use your existing SignalR connect logic
                const gameData = await response.json();
                renderPieces(gameData.pieces);
            } else {
                console.warn("Saved game not found or expired.");
            }
        } catch (err) {
            console.error("Failed to rejoin saved game:", err);
        }
    }
});