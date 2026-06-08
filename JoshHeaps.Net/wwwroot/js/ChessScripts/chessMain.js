async function forfeitCurrentGame() {
    const gameId = ChessUtils.getCookie("chessGameId");
    const playerId = ChessUtils.getCookie("chessPlayerId");

    if (!gameId || !playerId) return;

    try {
        await ChessAPI.forfeit(gameId, playerId);
    } catch (err) {
        console.warn("Could not forfeit previous game.", err);
    }
}

async function startNewGame() {
    await ChessSignalR.stopConnection();
    await forfeitCurrentGame();

    try {
        const gameData = await ChessAPI.joinGame();

        GameState.setGameInfo(gameData.gameId, gameData.id, gameData.isWhite);
        GameState.clearPreviousMove();

        ChessUtils.setCookie('chessGameId', gameData.gameId);
        ChessUtils.setCookie('chessPlayerId', gameData.id);
        ChessUtils.setCookie('chessPlayerIsWhite', gameData.isWhite);

        console.log("🆕 Game started:", gameData.gameId);

        await ChessSignalR.setupConnection();

        const gameState = await ChessAPI.getGameState(gameData.gameId);
        ChessBoard.renderPieces(gameState.pieces);

    } catch (error) {
        console.error("Failed to start new game:", error);
    }
}

async function startCPUGame() {
    await ChessSignalR.stopConnection();
    await forfeitCurrentGame();

    try {
        const difficulty = await ChessModals.promptDifficulty();
        const gameData = await ChessAPI.createCPUGame(difficulty);

        GameState.setGameInfo(gameData.gameId, gameData.id, gameData.isWhite);
        GameState.clearPreviousMove();

        ChessUtils.setCookie('chessGameId', gameData.gameId);
        ChessUtils.setCookie('chessPlayerId', gameData.id);
        ChessUtils.setCookie('chessPlayerIsWhite', gameData.isWhite);

        console.log("🆕 CPU Game started:", gameData.gameId);

        await ChessSignalR.setupConnection();

        const gameState = await ChessAPI.getGameState(gameData.gameId);
        ChessBoard.renderPieces(gameState.pieces);

    } catch (error) {
        console.error("Failed to start CPU game:", error);
    }
}

async function resumeSavedGame() {
    const savedGameId = ChessUtils.getCookie("chessGameId");
    const savedPlayerId = ChessUtils.getCookie("chessPlayerId");
    const savedPlayerIsWhite = ChessUtils.getCookie("chessPlayerIsWhite");

    if (savedGameId && savedPlayerId) {
        try {
            const gameState = await ChessAPI.getGameState(savedGameId);

            console.log("🧠 Rejoining saved game...");
            GameState.setGameInfo(savedGameId, savedPlayerId, savedPlayerIsWhite === "true");

            await ChessSignalR.setupConnection();
            ChessBoard.renderPieces(gameState.pieces);

        } catch (err) {
            console.warn("Saved game not found or expired.", err);
        }
    }
}

window.startNewGame = startNewGame;
window.startCPUGame = startCPUGame;

window.addEventListener('load', resumeSavedGame);

document.addEventListener('DOMContentLoaded', () => {
    ChessBoard.setupDragAndDrop();
});

console.log("chessMain.js loaded");