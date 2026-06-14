const Spectate = {
    connection: null,
    games: new Map(),   // gameId -> { isVsComputer, isComputerVsComputer, result }
    pgns: new Map(),    // gameId -> PGN text (prefetched when a game finishes)

    pieceTypeNames: ["Pawn", "Rook", "Knight", "Bishop", "Queen", "King"],

    async init() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl("/chessHub")
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        this.connection.on("ReceiveMoveUpdate", (gameId, moveDto, _moveResult, state) =>
            this.handleMoveUpdate(gameId, moveDto, state));

        this.connection.on("ReceiveGameOver", (gameId) => this.removeGame(gameId));

        try {
            await this.connection.start();
        } catch (err) {
            console.error("❌ SignalR failed to start:", err);
        }

        await this.refreshGames();
        await this.loadAutoTrainCount();
        setInterval(() => this.refreshGames(), 5000);
    },

    // Auto-training runs server-side; show its target count and let it be changed here.
    async loadAutoTrainCount() {
        const input = document.getElementById("autoTrainCount");
        if (!input) return;

        try {
            const response = await fetch("/api/chess/autotrain");
            const data = await response.json();
            input.max = data.max;
            // Don't clobber the value while the user is editing it.
            if (document.activeElement !== input)
                input.value = data.count;
        } catch {
            // Leave the control as-is if auto-training status can't be read.
        }
    },

    async setAutoTrainCount() {
        const input = document.getElementById("autoTrainCount");
        const count = Math.max(0, parseInt(input.value, 10) || 0);

        try {
            const response = await fetch(`/api/chess/autotrain?count=${count}`, { method: "POST" });
            const data = await response.json();
            input.value = data.count;
        } catch (err) {
            console.error("❌ Could not set the auto-training game count.", err);
        }
    },

    async startCpuGame() {
        const params = new URLSearchParams({
            whiteEngine: document.getElementById("whiteEngine").value,
            whiteSkill: document.getElementById("whiteSkill").value,
            blackEngine: document.getElementById("blackEngine").value,
            blackSkill: document.getElementById("blackSkill").value
        });

        try {
            await fetch(`/api/chess/watch/cpu?${params}`);
            await this.refreshGames();
        } catch (err) {
            console.error("❌ Could not start CPU vs CPU game.", err);
        }
    },

    async refreshGames() {
        let games;

        try {
            const response = await fetch("/api/chess/active");
            games = await response.json();
        } catch {
            return;
        }

        const activeIds = new Set(games.map(g => g.gameId));

        for (const gameId of [...this.games.keys()])
            if (!activeIds.has(gameId))
                await this.removeGame(gameId);

        for (const game of games)
            if (this.games.has(game.gameId))
                this.updateHeader(game.gameId, game.currentPlayer, game.moveCount, game.isCheck);
            else
                await this.addGame(game);

        const status = document.getElementById("watchStatus");
        status.textContent = games.length === 0
            ? "No games are being played right now."
            : `${games.length} game${games.length === 1 ? "" : "s"} in progress`;
    },

    async addGame(game) {
        this.games.set(game.gameId, {
            isVsComputer: game.isVsComputer,
            isComputerVsComputer: game.isComputerVsComputer,
            whiteEngine: game.whiteEngine,
            blackEngine: game.blackEngine
        });

        const card = document.createElement("div");
        card.className = "gameCard";
        card.id = `card-${game.gameId}`;
        card.onclick = () => this.enterFullscreen(game.gameId);

        const back = document.createElement("button");
        back.className = "backButton";
        back.textContent = "← Back";
        back.onclick = (event) => this.exitFullscreen(game.gameId, event);
        card.appendChild(back);

        const header = document.createElement("div");
        header.className = "gameCardHeader";
        header.id = `header-${game.gameId}`;
        this.renderHeader(header, this.games.get(game.gameId), game.currentPlayer, game.moveCount, game.isCheck);
        card.appendChild(header);

        const board = document.createElement("div");
        board.className = "miniBoard";

        for (let i = 0; i < 64; i++) {
            const square = document.createElement("div");
            square.id = `sq-${game.gameId}-${i}`;
            square.className = `chessSquare ${(i + Math.floor(i / 8)) % 2 === 0 ? "light" : "dark"}`;
            board.appendChild(square);
        }

        card.appendChild(board);
        document.getElementById("gamesFeed").appendChild(card);

        await this.connection.invoke("JoinWebsocketGroup", game.gameId).catch(() => { });
        await this.renderGame(game.gameId);
    },

    async removeGame(gameId) {
        this.games.delete(gameId);
        this.pgns.delete(gameId);
        document.getElementById(`card-${gameId}`)?.remove();
        await this.connection.invoke("LeaveWebsocketGroup", gameId).catch(() => { });
    },

    async renderGame(gameId) {
        const response = await fetch(`/api/chess/${gameId}`);

        if (!response.ok) return;

        this.renderFromState(gameId, await response.json());
    },

    renderFromState(gameId, state) {
        const result = this.resultTextFromState(state);
        const stored = this.games.get(gameId);

        if (stored) stored.result = result;

        this.renderPieces(gameId, state.pieces);
        this.updateHeader(gameId, state.currentPlayer, state.moveHistory.length, state.isCheck);
        this.setResult(gameId, result);
    },

    async handleMoveUpdate(gameId, moveDto, state) {
        if (!this.games.has(gameId)) return;

        // Render from the pushed state; fall back to a fetch only if it's missing.
        if (state) this.renderFromState(gameId, state);
        else await this.renderGame(gameId);

        this.highlightMove(gameId, moveDto);
    },

    renderPieces(gameId, pieces) {
        this.clearBoard(gameId);

        pieces.forEach(piece => {
            const square = document.getElementById(`sq-${gameId}-${piece.row * 8 + piece.col}`);

            if (!square) return;

            const img = document.createElement("img");
            img.src = this.pieceImageUrl(piece);
            img.alt = piece.type;
            img.className = "chessPiece";
            img.draggable = false;
            square.appendChild(img);
        });
    },

    clearBoard(gameId) {
        for (let i = 0; i < 64; i++) {
            const square = document.getElementById(`sq-${gameId}-${i}`);

            if (!square) continue;

            square.innerHTML = "";
            square.classList.remove("previous-start", "previous-end");
        }
    },

    highlightMove(gameId, moveDto) {
        document.getElementById(`sq-${gameId}-${moveDto.sourceRow * 8 + moveDto.sourceCol}`)?.classList.add("previous-start");
        document.getElementById(`sq-${gameId}-${moveDto.targetRow * 8 + moveDto.targetCol}`)?.classList.add("previous-end");
    },

    updateHeader(gameId, currentPlayer, moveCount, isCheck) {
        const stored = this.games.get(gameId);
        const header = document.getElementById(`header-${gameId}`);

        if (stored && header)
            this.renderHeader(header, stored, currentPlayer, moveCount, isCheck);
    },

    // Renders the header as a base text span plus a "check" tag that always occupies its slot
    // (hidden when not in check), so toggling check never re-centers or wraps the line.
    renderHeader(header, stored, currentPlayer, moveCount, isCheck) {
        const showCheck = !stored.result && isCheck;
        header.innerHTML = "";

        const main = document.createElement("span");
        main.textContent = this.headerText(stored, currentPlayer, moveCount);

        const tag = document.createElement("span");
        tag.className = showCheck ? "checkTag show" : "checkTag";
        tag.textContent = "• check";

        header.append(main, tag);
    },

    gameLabel(stored) {
        if (stored.isComputerVsComputer)
            return `${this.engineName(stored.whiteEngine)} (W) vs ${this.engineName(stored.blackEngine)} (B)`;
        if (stored.isVsComputer) return "Vs CPU";
        return "Player vs Player";
    },

    engineName(kind) {
        switch (kind) {
            case "CustomLearned": return "Learned";
            case "Custom": return "Custom";
            case "Stockfish": return "Stockfish";
            default: return kind || "CPU";
        }
    },

    headerText(stored, currentPlayer, moveCount) {
        if (stored.result)
            return `${this.gameLabel(stored)} · move ${moveCount} · final`;

        return `${this.gameLabel(stored)} · move ${moveCount} · ${currentPlayer} to move`;
    },

    resultTextFromState(state) {
        if (state.isCheckmate)
            return `${state.currentPlayer === "White" ? "Black" : "White"} wins by checkmate`;
        if (state.isStalemate)
            return "Draw — stalemate";
        if (state.isThreefoldRepetition)
            return "Draw — threefold repetition";
        return null;
    },

    setResult(gameId, text) {
        const card = document.getElementById(`card-${gameId}`);

        if (!card) return;

        // The result and Copy PGN button live in an overlay anchored over the board so showing
        // them at game end never changes the card's height (which would shift the whole grid).
        let overlay = card.querySelector(".gameOverlay");

        if (!text) {
            overlay?.remove();
            card.classList.remove("over");
            return;
        }

        if (!overlay) {
            overlay = document.createElement("div");
            overlay.className = "gameOverlay";

            const banner = document.createElement("div");
            banner.className = "gameResult";
            overlay.appendChild(banner);

            card.appendChild(overlay);
        }

        overlay.querySelector(".gameResult").textContent = text;
        card.classList.add("over");
        this.addCopyPgn(gameId, card);
    },

    addCopyPgn(gameId, card) {
        const overlay = card.querySelector(".gameOverlay");

        if (!overlay || overlay.querySelector(".copyPgnBtn")) return;

        const btn = document.createElement("button");
        btn.className = "copyPgnBtn";
        btn.textContent = "Copy PGN";
        btn.onclick = (event) => { event.stopPropagation(); this.copyPgn(gameId); };
        overlay.appendChild(btn);

        // Prefetch now (while the game is still in memory) so copy works during the
        // brief window before the finished game is cleaned up.
        fetch(`/api/chess/${gameId}/pgn`)
            .then(r => r.ok ? r.text() : null)
            .then(t => { if (t) this.pgns.set(gameId, t); })
            .catch(() => { });
    },

    async copyPgn(gameId) {
        let pgn = this.pgns.get(gameId);

        if (!pgn) {
            try {
                const r = await fetch(`/api/chess/${gameId}/pgn`);
                if (r.ok) pgn = await r.text();
            } catch { /* ignore */ }
        }

        if (!pgn) {
            alert("PGN is no longer available for this game.");
            return;
        }

        try {
            await navigator.clipboard.writeText(pgn);
            alert("📋 PGN copied to clipboard!");
        } catch {
            alert("Couldn't access the clipboard. Here's the PGN:\n\n" + pgn);
        }
    },

    enterFullscreen(gameId) {
        document.getElementById(`card-${gameId}`)?.classList.add("fullscreen");
        document.body.classList.add("fullscreen-open");
    },

    exitFullscreen(gameId, event) {
        event?.stopPropagation();
        document.getElementById(`card-${gameId}`)?.classList.remove("fullscreen");
        document.body.classList.remove("fullscreen-open");
    },

    pieceImageUrl(piece) {
        const color = piece.color === 0 ? "White" : "Black";
        return `/images/Chess Images/${color}${this.pieceTypeNames[piece.type]}.svg`;
    }
};

window.addEventListener("load", () => Spectate.init());

console.log("Spectate.js loaded");
