/*
 * Round lifecycle: join a room, chirp when told to, measure every chirp in our own recording, and
 * report the frame indices. Peaks are reported relative to the analysis window rather than to an
 * absolute frame — every measurement is a difference within one device's own recording, so a
 * constant per-device origin cancels.
 *
 * Both the chirp and the analysis are driven by the audio clock rather than by timers: a hidden tab
 * has its timers throttled to roughly once a second, which is long enough to put its chirp in
 * another device's slot and turn the whole round into nonsense.
 */
const EchoSession = {
    LEAD_IN_SECONDS: 0.25,
    TEMPERATURE_CELSIUS: 20,
    MAX_OUTPUT_LATENCY_SECONDS: 0.35,
    CHIRP: { durationSeconds: 0.05, startHz: 2000, endHz: 8000 },

    CLOCK_SAMPLES: 5,
    LATE_TOLERANCE_SECONDS: 0.05,

    connection: null,
    deviceId: null,
    roomCode: null,
    serverOffsetMs: 0,
    skippedRounds: 0,
    devices: [],
    chirp: null,
    pending: null,
    lastRound: null,
    hintsByDevice: {},
    previousPoints: null,
    epsilon: 0.08,
    onUpdate: () => {},

    async join(roomCode, displayName, { onUpdate }) {
        this.onUpdate = onUpdate ?? this.onUpdate;
        const audio = await EchoAudio.start();
        this.chirp = EchoDsp.makeChirp({ sampleRate: audio.sampleRate, ...this.CHIRP });
        EchoAudio.onSamples = frame => this.analyseIfCaptured(frame);

        this.connection = new signalR.HubConnectionBuilder().withUrl("/echoHub").withAutomaticReconnect().build();
        this.connection.on("RoomChanged", room => this.handleRoomChanged(room));
        this.connection.on("RoundStarting", schedule => this.handleRoundStarting(schedule));
        this.connection.on("RoundComplete", result => this.handleRoundComplete(result));
        await this.connection.start();

        const joined = await this.connection.invoke("JoinRoom", roomCode, displayName, audio.sampleRate);
        this.deviceId = joined.deviceId;
        this.roomCode = joined.roomCode;
        this.devices = joined.room.devices;
        this.serverOffsetMs = await this.estimateServerOffset();

        return { ...joined, audio, serverOffsetMs: this.serverOffsetMs };
    },

    /**
     * Offset from the server clock, by the usual round-trip midpoint. Only needs to be good enough
     * to identify which chirp belongs to which slot, which is tens of milliseconds — the ranging
     * itself never uses a shared clock.
     */
    async estimateServerOffset() {
        const offsets = [];

        for (let attempt = 0; attempt < this.CLOCK_SAMPLES; attempt++) {
            const sent = Date.now();
            const serverNow = await this.connection.invoke("ServerTime");
            const received = Date.now();
            offsets.push(serverNow - (sent + received) / 2);
        }

        offsets.sort((left, right) => left - right);
        return offsets[offsets.length >> 1];
    },

    serverNowMs() {
        return Date.now() + this.serverOffsetMs;
    },

    handleRoomChanged(room) {
        this.devices = room.devices;
        this.previousPoints = null;
        this.hintsByDevice = {};
        this.onUpdate({ kind: "room", room });
        return room;
    },

    /**
     * Schedule our chirp for our slot on the audio clock, and record which frames this round will
     * occupy so the analysis can start the moment that audio has actually been captured.
     */
    handleRoundStarting(schedule) {
        const ownSlot = schedule.slotOrder.indexOf(this.deviceId);
        if (ownSlot < 0 || !EchoAudio.context) return null;

        const sampleRate = EchoAudio.context.sampleRate;
        const slotSeconds = schedule.slotMilliseconds / 1000;
        const secondsUntilStart = (schedule.startsAtUnixMs - this.serverNowMs()) / 1000;
        const roundStartTime = EchoAudio.context.currentTime + secondsUntilStart;
        const playAtTime = roundStartTime + ownSlot * slotSeconds;

        // Sitting a round out is far better than chirping late: a chirp in the wrong slot is
        // attributed to the wrong device and ruins the round for everyone, whereas a missing chirp
        // costs only this pair, this round.
        if (playAtTime < EchoAudio.context.currentTime - this.LATE_TOLERANCE_SECONDS) {
            this.skippedRounds++;
            this.onUpdate({ kind: "skipped", schedule, lateBySeconds: EchoAudio.context.currentTime - playAtTime });
            return null;
        }

        const scheduledFrame = EchoAudio.play(this.chirp, playAtTime);

        const round = {
            schedule,
            ownSlot,
            sampleRate,
            scheduledFrame,
            slotSamples: Math.round(slotSeconds * sampleRate),
            leadInSamples: Math.round(this.LEAD_IN_SECONDS * sampleRate),
            startFrame: EchoAudio.frameAt(roundStartTime),
            outputLatencyMs: Math.round(EchoAudio.outputLatencySeconds() * 1000),
            captureLagMs: Math.round(EchoAudio.captureLagSeconds() * 1000)
        };

        round.windowStart = round.startFrame - round.leadInSamples;
        round.windowLength =
            round.leadInSamples +
            schedule.slotOrder.length * round.slotSamples +
            Math.round((schedule.tailMilliseconds / 1000) * sampleRate);

        this.pending = round;
        this.onUpdate({ kind: "roundStarting", round });
        return round;
    },

    /**
     * Run as soon as the round's audio exists in the ring. Driven by captured audio rather than a
     * timer so a throttled tab still analyses on time.
     */
    analyseIfCaptured(highestFrame) {
        const round = this.pending;
        if (!round) return null;
        if (highestFrame < round.windowStart + round.windowLength) return null;

        this.pending = null;
        return this.analyse(round);
    },

    analyse(round) {
        this.lastRound = round;
        const recording = EchoAudio.read(round.windowStart, round.windowLength);
        if (!recording) return null;

        const envelope = EchoDsp.matchedFilterEnvelope(recording, this.chirp);
        const detected = EchoDsp.detectSlotPeaks({
            envelope,
            slotCount: round.schedule.slotOrder.length,
            slotSamples: round.slotSamples,
            ownSlot: round.ownSlot,
            // Anchored on the frame the chirp was scheduled for, so the only unknown left is how
            // long the speaker takes to actually emit it.
            ownSearchStart: round.scheduledFrame - round.windowStart,
            ownSearchSamples: Math.round((this.MAX_OUTPUT_LATENCY_SECONDS + this.CHIRP.durationSeconds) * round.sampleRate),
            slotHints: this.hintsFor(round.schedule)
        });

        const diagnostics = this.describeRound(round, detected);
        this.onUpdate({ kind: "envelope", envelope, detected, round, diagnostics });
        if (!detected) return null;

        this.rememberHints(round.schedule, detected, round.slotSamples);
        return this.report(round, detected);
    },

    describeRound(round, detected) {
        const millisecondsPer = 1000 / round.sampleRate;

        return {
            captureLagMs: round.captureLagMs,
            outputLatencyMs: round.outputLatencyMs,
            serverOffsetMs: Math.round(this.serverOffsetMs),
            skippedRounds: this.skippedRounds,
            clipping: EchoAudio.peakAmplitude >= 0.99,
            inputPeak: Number(EchoAudio.peakAmplitude.toFixed(3)),
            ownFound: Boolean(detected?.peaks[round.ownSlot]),
            slots: round.schedule.slotOrder.map((deviceId, slot) => ({
                deviceId,
                own: slot === round.ownSlot,
                residualMs: detected?.peaks[slot]
                    ? Math.round((detected.peaks[slot].index - (detected.anchor + slot * round.slotSamples)) * millisecondsPer)
                    : null,
                snr: detected?.peaks[slot] ? Math.round(detected.peaks[slot].snr) : null
            }))
        };
    },

    report(round, detected) {
        return this.connection.invoke("ReportRound", {
            deviceId: this.deviceId,
            roundId: round.schedule.roundId,
            slot: round.ownSlot,
            sampleRate: round.sampleRate,
            epsilon: this.epsilon,
            peaks: detected.peaks.map(peak => peak?.index ?? null)
        });
    },

    /**
     * Where each device's chirp actually landed last round, relative to where the slot said it
     * would. Output latency differs by tens of milliseconds per device and is stable, so carrying
     * the residual forward keeps the search windows centred instead of merely wide.
     */
    rememberHints(schedule, detected, slotSamples) {
        schedule.slotOrder.forEach((deviceId, slot) => {
            const peak = detected.peaks[slot];
            if (!peak) return;
            this.hintsByDevice[deviceId] = peak.index - (detected.anchor + slot * slotSamples);
        });

        return this.hintsByDevice;
    },

    hintsFor(schedule) {
        return schedule.slotOrder.map(deviceId => this.hintsByDevice[deviceId] ?? 0);
    },

    handleRoundComplete(result) {
        const reports = result.reports.map(report => ({ ...report, peaks: report.peaks ?? [] }));
        const solved = EchoDsp.solveRound(reports, {
            speedOfSound: EchoDsp.speedOfSound(this.TEMPERATURE_CELSIUS),
            previousPoints: this.previousPoints
        });

        this.previousPoints = this.pointsByReportIndex(reports, solved);
        this.onUpdate({ kind: "solved", result, reports, solved, deviceId: this.deviceId });
        return solved;
    },

    pointsByReportIndex(reports, solved) {
        const points = new Array(reports.length).fill(null);
        solved.keep.forEach((reportIndex, i) => {
            points[reportIndex] = solved.points[i];
        });
        return points;
    },

    setEpsilon(metres) {
        this.epsilon = metres;
        localStorage.setItem("echo.epsilon", String(metres));
        return this.epsilon;
    },

    loadEpsilon() {
        const stored = Number(localStorage.getItem("echo.epsilon"));
        this.epsilon = Number.isFinite(stored) && stored > 0 ? stored : 0.08;
        return this.epsilon;
    },

    async leave() {
        await this.connection?.invoke("LeaveRoom").catch(() => {});
        await this.connection?.stop();
        await EchoAudio.stop();
        this.connection = null;
        this.pending = null;
        this.previousPoints = null;
        this.hintsByDevice = {};
        return true;
    }
};

if (typeof window !== "undefined") window.EchoSession = EchoSession;
