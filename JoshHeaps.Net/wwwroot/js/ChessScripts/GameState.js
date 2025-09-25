const GameState = {
    currentGameId: null,
    currentPlayerId: null,
    currentPlayerIsWhite: null,
    selectedPiece: null,
    legalMoves: [],
    previousMoveStart: null,
    previousMoveEnd: null,

    reset() {
        this.currentGameId = null;
        this.currentPlayerId = null;
        this.currentPlayerIsWhite = null;
        this.selectedPiece = null;
        this.legalMoves = [];
        this.previousMoveStart = null;
        this.previousMoveEnd = null;
    },

    setGameInfo(gameId, playerId, isWhite) {
        this.currentGameId = gameId;
        this.currentPlayerId = playerId;
        this.currentPlayerIsWhite = isWhite;
    },

    setSelectedPiece(piece) {
        this.selectedPiece = piece;
    },

    clearSelection() {
        this.selectedPiece = null;
        this.legalMoves = [];
    },

    setLegalMoves(moves) {
        this.legalMoves = moves;
    },

    setPreviousMove(startPos, endPos) {
        this.previousMoveStart = startPos;
        this.previousMoveEnd = endPos;
    },

    clearPreviousMove() {
        this.previousMoveStart = null;
        this.previousMoveEnd = null;
    }
};

console.log("GameState.js loaded");