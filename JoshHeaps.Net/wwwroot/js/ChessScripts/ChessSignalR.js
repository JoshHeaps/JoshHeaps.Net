const ChessSignalR = {
    connection: null,

    async setupConnection() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl("/chessHub")
            .configureLogging(signalR.LogLevel.Information)
            .build();

        this.connection.onclose(err => {
            console.error("❌ SignalR connection closed:", err?.message);
        });

        this.connection.on("ReceiveMoveUpdate", async (gameId, moveDto, moveResultDto, state) => {
            await this.handleMoveUpdate(gameId, moveDto, moveResultDto, state);
        });

        this.connection.on("ReceiveGameOver", async (gameId, winner, reason) => {
            await this.handleGameOver(gameId, winner, reason);
        });

        try {
            await this.connection.start();
            console.log("✅ SignalR connected");
            await this.connection.invoke("JoinWebsocketGroup", GameState.currentGameId);
        } catch (err) {
            console.error("❌ SignalR failed to start or join:", err);
        }
    },

    async handleMoveUpdate(gameId, moveDto, moveResultDto, state) {
        if (gameId !== GameState.currentGameId) return;

        // Drop the echo of our own move and any out-of-order delivery.
        if (!GameState.shouldApply(state?.version)) return;

        GameState.setPreviousMove([moveDto.sourceRow, moveDto.sourceCol], [moveDto.targetRow, moveDto.targetCol]);
        ChessBoard.renderState(state);

        ChessAPI.alertGameStatusChange(moveResultDto);
    },

    async handleGameOver(gameId, winner, reason) {
        if (gameId !== GameState.currentGameId) return;

        const youWon = (winner === "White") === GameState.currentPlayerIsWhite;

        if (reason === "forfeit")
            alert(youWon ? "🏳️ Your opponent forfeited — you win!" : "🏳️ You forfeited this game.");

        showCopyPgn(gameId);
        await this.leaveGame();
    },

    async leaveGame() {
        if (this.connection) {
            await this.connection.invoke("LeaveWebsocketGroup", GameState.currentGameId);
            await this.connection.stop();
            this.connection = null;
        }
    },

    async stopConnection() {
        if (this.connection) {
            await this.connection.stop();
            this.connection = null;
        }
    }
};

console.log("ChessSignalR.js loaded");