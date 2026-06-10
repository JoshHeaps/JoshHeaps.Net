const GameState = {
    currentGameId: null,
    currentPlayerId: null,
    currentPlayerIsWhite: null,
    selectedPiece: null,
    legalMoves: [],
    previousMoveStart: null,
    previousMoveEnd: null,
    // Highest ply (move count) already rendered. Lets us ignore stale or
    // already-applied updates that arrive out of order, including the echo
    // of our own move.
    lastVersion: -1,

    reset() {
        this.currentGameId = null;
        this.currentPlayerId = null;
        this.currentPlayerIsWhite = null;
        this.selectedPiece = null;
        this.legalMoves = [];
        this.previousMoveStart = null;
        this.previousMoveEnd = null;
        this.lastVersion = -1;
    },

    shouldApply(version) {
        return typeof version !== "number" || version > this.lastVersion;
    },

    setVersion(version) {
        if (typeof version === "number") this.lastVersion = version;
    },

    setGameInfo(gameId, playerId, isWhite) {
        this.currentGameId = gameId;
        this.currentPlayerId = playerId;
        this.currentPlayerIsWhite = isWhite;
        this.lastVersion = -1;
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