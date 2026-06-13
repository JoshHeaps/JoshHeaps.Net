const Weights = {
    async init() {
        await this.refresh();
    },

    async refresh() {
        let data;

        try {
            const response = await fetch("/api/chess/weights");
            data = await response.json();
        } catch (err) {
            console.error("❌ Could not load weights.", err);
            document.getElementById("weightsStatus").textContent = "Could not load weights.";
            return;
        }

        this.renderBoardSet(data.mg, "weightsGridMg");
        this.renderBoardSet(data.eg, "weightsGridEg");
        this.renderFeatures(data.features);

        const trained = [...data.mg, ...data.eg].some(b => b.squares.some(v => v !== 0))
            || data.features.some(f => f.value !== 0);

        document.getElementById("weightsStatus").textContent = trained
            ? "Where the learned engine thinks each piece belongs. Blue = preferred, red = avoided. Midgame vs endgame tables are blended by how much material is left."
            : "No training yet — everything is neutral. Run some learned games on the Watch page.";
    },

    // pieces: [{ name, squares[64] }]. Prepends an "Overall" board summing the set.
    renderBoardSet(pieces, containerId) {
        const overall = new Array(64).fill(0);
        for (const piece of pieces)
            for (let sq = 0; sq < 64; sq++)
                overall[sq] += piece.squares[sq];

        const boards = [{ name: "Overall", squares: overall }, ...pieces];
        const grid = document.getElementById(containerId);
        grid.innerHTML = "";
        boards.forEach(board => grid.appendChild(this.buildBoard(board)));
    },

    buildBoard(board) {
        const wrapper = document.createElement("div");
        wrapper.className = "weightBoard";

        const maxAbs = board.squares.reduce((m, v) => Math.max(m, Math.abs(v)), 0);

        const header = document.createElement("div");
        header.className = "weightBoardHeader";
        if (board.name !== "Overall") {
            const icon = document.createElement("img");
            icon.src = `/images/Chess Images/White${board.name}.svg`;
            icon.alt = board.name;
            icon.className = "weightBoardIcon";
            header.appendChild(icon);
        }
        const title = document.createElement("span");
        title.textContent = board.name;
        header.appendChild(title);
        const range = document.createElement("span");
        range.className = "weightRange";
        range.textContent = maxAbs === 0 ? "neutral" : `±${maxAbs}`;
        header.appendChild(range);
        wrapper.appendChild(header);

        const heat = document.createElement("div");
        heat.className = "miniHeat";

        for (let row = 0; row < 8; row++) {
            const rankLabel = document.createElement("div");
            rankLabel.className = "heatLabel";
            rankLabel.textContent = 8 - row;          // rank 8 at top, 1 at bottom
            heat.appendChild(rankLabel);

            for (let col = 0; col < 8; col++) {
                const rank = 7 - row;                 // rank index, 0 = rank 1
                const sq = rank * 8 + col;            // white-relative square (A1 = 0)
                heat.appendChild(this.buildSquare(board.squares[sq], sq, maxAbs));
            }
        }

        heat.appendChild(this.cornerSpacer());
        for (let col = 0; col < 8; col++) {
            const fileLabel = document.createElement("div");
            fileLabel.className = "heatLabel";
            fileLabel.textContent = String.fromCharCode(97 + col);
            heat.appendChild(fileLabel);
        }

        wrapper.appendChild(heat);
        return wrapper;
    },

    buildSquare(value, sq, maxAbs) {
        const cell = document.createElement("div");
        cell.className = "heatSquare";

        if (value !== 0 && maxAbs > 0) {
            const ratio = Math.abs(value) / maxAbs;
            const alpha = (0.12 + 0.88 * ratio).toFixed(3);
            cell.style.backgroundColor = value > 0
                ? `rgba(74, 134, 232, ${alpha})`      // high -> blue
                : `rgba(232, 74, 74, ${alpha})`;      // low  -> red
            cell.textContent = value;
        }

        const file = String.fromCharCode(97 + (sq & 7));
        const rank = (sq >> 3) + 1;
        cell.title = `${file}${rank}: ${value}`;
        return cell;
    },

    cornerSpacer() {
        const spacer = document.createElement("div");
        spacer.className = "heatLabel";
        return spacer;
    },

    // features: [{ name, value }]
    renderFeatures(features) {
        const panel = document.getElementById("featureWeights");
        panel.innerHTML = "";

        const maxAbs = features.reduce((m, f) => Math.max(m, Math.abs(f.value)), 0);

        features.forEach(feature => {
            const row = document.createElement("div");
            row.className = "featureRow";

            const label = document.createElement("span");
            label.className = "featureLabel";
            label.textContent = feature.name;

            const track = document.createElement("div");
            track.className = "featureTrack";
            const bar = document.createElement("div");
            bar.className = "featureBar";
            const ratio = maxAbs === 0 ? 0 : Math.abs(feature.value) / maxAbs;
            bar.style.width = `${(ratio * 100).toFixed(1)}%`;
            bar.style.backgroundColor = feature.value >= 0
                ? "rgba(74, 134, 232, 0.85)"
                : "rgba(232, 74, 74, 0.85)";
            track.appendChild(bar);

            const value = document.createElement("span");
            value.className = "featureValue";
            value.textContent = feature.value;

            row.append(label, track, value);
            panel.appendChild(row);
        });
    }
};

window.addEventListener("load", () => Weights.init());

console.log("Weights.js loaded");
