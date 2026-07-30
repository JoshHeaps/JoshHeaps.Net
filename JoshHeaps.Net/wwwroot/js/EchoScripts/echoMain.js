/*
 * Page wiring for /echo. Renders what the session measures: the matched-filter trace with the
 * arrivals it picked, the pairwise ranges, and — while only two devices are present — one large
 * number, because that number is the whole measurement.
 */
const EchoPage = {
    HISTORY_LENGTH: 12,

    elements: {},
    lastSolved: null,
    lastDiagnostics: null,
    history: [],
    roundsSeen: 0,

    start() {
        this.elements = {
            room: document.getElementById("echoRoom"),
            name: document.getElementById("echoName"),
            join: document.getElementById("echoJoin"),
            leave: document.getElementById("echoLeave"),
            status: document.getElementById("echoStatus"),
            warnings: document.getElementById("echoWarnings"),
            readout: document.getElementById("echoReadout"),
            pairs: document.getElementById("echoPairs"),
            roster: document.getElementById("echoRoster"),
            trace: document.getElementById("echoTrace"),
            epsilon: document.getElementById("echoEpsilon"),
            shareLink: document.getElementById("echoShareLink"),
            diagnostics: document.getElementById("echoDiagnostics")
        };

        this.elements.room.value = new URLSearchParams(location.search).get("room") ?? this.randomCode();
        this.elements.epsilon.value = EchoSession.loadEpsilon();
        this.elements.epsilon.addEventListener("change", () => this.applyEpsilon());
        this.elements.join.addEventListener("click", () => this.join());
        this.elements.leave.addEventListener("click", () => this.leave());

        return this;
    },

    randomCode() {
        const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return Array.from({ length: 4 }, () => alphabet[Math.floor(Math.random() * alphabet.length)]).join("");
    },

    applyEpsilon() {
        const metres = Number(this.elements.epsilon.value);
        if (!Number.isFinite(metres) || metres < 0) return null;

        EchoSession.setEpsilon(metres);
        return metres;
    },

    async join() {
        const roomCode = this.elements.room.value.trim().toUpperCase();
        if (!roomCode) return null;

        this.setStatus("Requesting the microphone…");
        this.elements.join.disabled = true;

        try {
            const joined = await EchoSession.join(roomCode, this.deviceName(), { onUpdate: update => this.handle(update) });
            this.showJoined(joined);
            return joined;
        } catch (error) {
            this.setStatus(`Could not start: ${error.message}`);
            this.elements.join.disabled = false;
            return null;
        }
    },

    deviceName() {
        const typed = this.elements.name.value.trim();
        if (typed) return typed;

        return /android|iphone|ipad|mobile/i.test(navigator.userAgent) ? "phone" : "laptop";
    },

    showJoined(joined) {
        this.elements.leave.hidden = false;
        this.elements.room.disabled = true;
        this.elements.name.disabled = true;
        this.elements.shareLink.textContent = `${location.origin}/echo?room=${joined.roomCode}`;
        this.elements.shareLink.href = `/echo?room=${joined.roomCode}`;
        this.renderWarnings(joined.audio);
        this.renderRoster(joined.room.devices);
        this.setStatus(`Listening at ${joined.audio.sampleRate}Hz. Open the same room on another device.`);
        return joined;
    },

    async leave() {
        await EchoSession.leave();
        this.elements.leave.hidden = true;
        this.elements.join.disabled = false;
        this.elements.room.disabled = false;
        this.elements.name.disabled = false;
        this.setStatus("Left the room.");
        return true;
    },

    handle(update) {
        if (update.kind === "room") return this.renderRoster(update.room.devices);
        if (update.kind === "envelope") {
            this.renderDiagnostics(update.diagnostics);
            return this.renderTrace(update);
        }
        if (update.kind === "solved") return this.renderSolved(update);
        return null;
    },

    /**
     * Per-slot residual is the diagnostic that matters: it is how far each chirp landed from where
     * its slot said it would. Steady residuals mean the arrivals are being attributed correctly;
     * residuals jumping by more than a slot mean they are not, and every range is then meaningless.
     */
    renderDiagnostics(diagnostics) {
        if (!diagnostics) return null;
        this.lastDiagnostics = diagnostics;

        const slots = diagnostics.slots
            .map(slot => {
                const label = slot.own ? "self" : slot.deviceId;
                const residual = slot.residualMs === null ? "missed" : `${slot.residualMs > 0 ? "+" : ""}${slot.residualMs}ms`;
                return `<li class="${slot.residualMs === null ? "echo-missed" : ""}">${label} · ${residual}${slot.snr === null ? "" : ` · snr ${slot.snr}`}</li>`;
            })
            .join("");

        this.elements.diagnostics.innerHTML = `
            <ul class="echo-slots">${slots}</ul>
            <p class="echo-diag-line">
                capture lag ${diagnostics.captureLagMs}ms · output latency ${diagnostics.outputLatencyMs}ms ·
                clock offset ${diagnostics.serverOffsetMs}ms · rounds sat out ${diagnostics.skippedRounds} ·
                input peak ${diagnostics.inputPeak}${diagnostics.clipping ? " <strong>CLIPPING</strong>" : ""}
            </p>`;

        return diagnostics;
    },

    recordHistory(metres) {
        this.history.push(metres);
        if (this.history.length > this.HISTORY_LENGTH) this.history.shift();

        const spread = Math.max(...this.history) - Math.min(...this.history);
        return { spread, count: this.history.length };
    },

    setStatus(message) {
        this.elements.status.textContent = message;
        return message;
    },

    renderWarnings(audio) {
        this.elements.warnings.innerHTML = "";

        for (const warning of audio.warnings) {
            const item = document.createElement("p");
            item.className = "echo-warning";
            item.textContent = warning;
            this.elements.warnings.appendChild(item);
        }

        return audio.warnings.length;
    },

    renderRoster(devices) {
        this.elements.roster.innerHTML = "";

        for (const device of devices) {
            const row = document.createElement("li");
            row.className = device.deviceId === EchoSession.deviceId ? "echo-device echo-device-self" : "echo-device";
            row.textContent = `${device.displayName} · ${device.sampleRate}Hz`;
            this.elements.roster.appendChild(row);
        }

        if (devices.length < 2) this.setStatus("Waiting for a second device to join this room.");
        return devices.length;
    },

    renderSolved({ result, reports, solved }) {
        this.roundsSeen++;
        const raw = EchoDsp.buildDistanceMatrix(
            reports.map(report => ({ ...report, epsilon: 0 })),
            { speedOfSound: EchoDsp.speedOfSound(EchoSession.TEMPERATURE_CELSIUS) }
        );

        this.lastSolved = { result, reports, solved, raw };
        this.renderReadout(reports, solved, raw);
        this.renderPairs(reports, solved, raw);
        this.setStatus(`Round ${this.roundsSeen} · ${reports.length} of ${EchoSession.devices.length} devices reported`);
        return solved;
    },

    renderReadout(reports, solved, raw) {
        const readout = this.elements.readout;

        if (reports.length !== 2 || solved.matrix[0][1] === null) {
            readout.innerHTML = `<span class="echo-readout-idle">${reports.length < 2 ? "waiting for a pair" : "measuring…"}</span>`;
            return null;
        }

        const corrected = solved.matrix[0][1];
        const { spread, count } = this.recordHistory(corrected);
        readout.innerHTML = `
            <span class="echo-metres">${corrected.toFixed(2)}<small>m</small></span>
            <span class="echo-readout-detail">
                raw ${raw[0][1].toFixed(3)}m · calibration +${(corrected - raw[0][1]).toFixed(3)}m ·
                spread over last ${count} ${spread.toFixed(2)}m
            </span>`;

        return corrected;
    },

    renderPairs(reports, solved, raw) {
        const rows = [];

        for (let i = 0; i < reports.length; i++)
            for (let j = i + 1; j < reports.length; j++) {
                const dropped = !solved.keep.includes(i) || !solved.keep.includes(j);
                const measured = solved.matrix[i][j];
                rows.push(`<tr class="${dropped ? "echo-dropped" : ""}">
                    <td>${reports[i].deviceId} ↔ ${reports[j].deviceId}</td>
                    <td>${measured === null ? "—" : measured.toFixed(3) + " m"}</td>
                    <td>${raw[i][j] === null ? "—" : raw[i][j].toFixed(3) + " m"}</td>
                </tr>`);
            }

        this.elements.pairs.innerHTML = rows.length
            ? `<table><thead><tr><th>pair</th><th>range</th><th>raw</th></tr></thead><tbody>${rows.join("")}</tbody></table>`
            : "";

        return rows.length;
    },

    /** The matched-filter trace, with a marker on each arrival the detector accepted. */
    renderTrace({ envelope, detected, round }) {
        const canvas = this.elements.trace;
        const context = canvas.getContext("2d");
        const width = (canvas.width = canvas.clientWidth);
        const height = (canvas.height = 160);
        const peak = EchoDsp.maxInRange(envelope, 0, envelope.length).value || 1;

        context.clearRect(0, 0, width, height);
        context.strokeStyle = "rgba(9, 255, 0, 0.75)";
        context.beginPath();

        const bucket = envelope.length / width;
        for (let x = 0; x < width; x++) {
            const start = Math.floor(x * bucket);
            const highest = EchoDsp.maxInRange(envelope, start, Math.min(envelope.length, Math.floor(start + bucket))).value;
            const y = height - (highest / peak) * (height - 8) - 4;
            x === 0 ? context.moveTo(x, y) : context.lineTo(x, y);
        }

        context.stroke();
        this.drawMarkers(context, detected, round, envelope.length, width, height);
        return canvas;
    },

    drawMarkers(context, detected, round, envelopeLength, width, height) {
        if (!detected) return null;

        context.font = "11px 'Cascadia Code', monospace";

        detected.peaks.forEach((peak, slot) => {
            if (!peak) return;

            const x = (peak.index / envelopeLength) * width;
            const own = slot === round.ownSlot;
            context.strokeStyle = own ? "#5fff5f" : "rgba(255, 255, 255, 0.55)";
            context.fillStyle = context.strokeStyle;
            context.beginPath();
            context.moveTo(x, 0);
            context.lineTo(x, height);
            context.stroke();
            context.fillText(own ? `self (${slot})` : `slot ${slot}`, x + 4, 14 + slot * 13);
        });

        return detected.peaks.length;
    }
};

document.addEventListener("DOMContentLoaded", () => EchoPage.start());
