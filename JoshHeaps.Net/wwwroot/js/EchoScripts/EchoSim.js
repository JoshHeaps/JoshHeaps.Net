/*
 * Virtual room for exercising the real ranging pipeline without microphones. Synthesizes what
 * each device would have recorded — propagation delay, reflections, noise, per-device clock offset
 * and unknown output latency — then runs the same EchoDsp code the page runs.
 */
const EchoSim = {
    DEFAULTS: {
        sampleRate: 48000,
        slotSeconds: 0.4,
        tailSeconds: 0.5,
        speedOfSound: 343,
        noiseAmplitude: 0.01,
        referenceGain: 0.5,
        minimumPathMetres: 0.25,
        maximumOutputLatencySeconds: 0.2,
        chirp: { durationSeconds: 0.05, startHz: 2000, endHz: 8000 },
        reflectionsPerPath: 2,
        reflectionExtraRange: [0.4, 4.0],
        reflectionGainRange: [0.2, 0.8],
        seed: 20260730
    },

    randomGenerator(seed) {
        let state = seed >>> 0;

        return () => {
            state = (state + 0x6d2b79f5) >>> 0;
            let mixed = Math.imul(state ^ (state >>> 15), 1 | state);
            mixed = (mixed + Math.imul(mixed ^ (mixed >>> 7), 61 | mixed)) ^ mixed;
            return ((mixed ^ (mixed >>> 14)) >>> 0) / 4294967296;
        };
    },

    separation(first, second) {
        return Math.hypot(first[0] - second[0], first[1] - second[1]);
    },

    buildConfiguration(overrides = {}) {
        const config = { ...this.DEFAULTS, ...overrides };
        const count = config.positions.length;

        config.chirp = { ...this.DEFAULTS.chirp, ...(overrides.chirp ?? {}) };
        config.epsilon ??= new Array(count).fill(0.05);
        config.clockOffsets ??= config.positions.map((_, i) => i * 7919);
        config.outputLatencies ??= config.positions.map((_, i) => 0.02 + 0.03 * i);
        config.scheduleJitter ??= config.positions.map((_, i) => 0.004 * i);
        config.reflections ??= this.buildReflectionTable(config);
        return config;
    },

    /**
     * Multipath for every source-to-listener path independently. Giving every path the same echo
     * would be worthless as a test: an identical bias on all four arrivals cancels out of the
     * range formula, so a uniform echo model hides exactly the error it is supposed to expose.
     */
    buildReflectionTable(config) {
        const random = this.randomGenerator(config.seed ^ 0x5f3759df);
        const spread = (range, value) => range[0] + value * (range[1] - range[0]);

        return config.positions.map(() =>
            config.positions.map(() =>
                Array.from({ length: config.reflectionsPerPath }, () => ({
                    extraMetres: spread(config.reflectionExtraRange, random()),
                    gain: spread(config.reflectionGainRange, random())
                }))
            )
        );
    },

    reflectionsFor(config, source, listener) {
        return Array.isArray(config.reflections[0]) ? config.reflections[source][listener] : config.reflections;
    },

    /** One recording per device, plus the index each device believes it started playing at. */
    synthesizeRound(config) {
        const { positions, sampleRate, slotSeconds, tailSeconds } = config;
        const random = this.randomGenerator(config.seed);
        const template = EchoDsp.makeChirp({ sampleRate, ...config.chirp });
        const maximumOffset = Math.max(...config.clockOffsets);
        const length = Math.ceil((positions.length * slotSeconds + tailSeconds) * sampleRate) + maximumOffset;

        const devices = positions.map((_, index) => ({
            recording: this.noiseBuffer(length, config.noiseAmplitude, random),
            ownSearchStart: Math.round((index * slotSeconds + config.scheduleJitter[index]) * sampleRate) + config.clockOffsets[index]
        }));

        for (let source = 0; source < positions.length; source++)
            for (let listener = 0; listener < positions.length; listener++)
                this.mixArrivals(devices[listener].recording, template, config, source, listener);

        return { devices, template };
    },

    noiseBuffer(length, amplitude, random) {
        const buffer = new Float32Array(length);
        for (let i = 0; i < length; i++) buffer[i] = (random() * 2 - 1) * amplitude;
        return buffer;
    },

    mixArrivals(recording, template, config, source, listener) {
        const emissionSeconds =
            source * config.slotSeconds + config.scheduleJitter[source] + config.outputLatencies[source];
        const directMetres =
            source === listener ? config.epsilon[source] : this.separation(config.positions[source], config.positions[listener]);

        const paths = [
            { metres: directMetres, gain: 1 },
            ...this.reflectionsFor(config, source, listener).map(({ extraMetres, gain }) => ({
                metres: directMetres + extraMetres,
                gain
            }))
        ];

        for (const path of paths) {
            const arrival = emissionSeconds + path.metres / config.speedOfSound;
            const amplitude =
                (config.referenceGain / Math.max(path.metres, config.minimumPathMetres)) * path.gain;
            this.addAt(recording, template, Math.round(arrival * config.sampleRate) + config.clockOffsets[listener], amplitude);
        }

        return recording;
    },

    addAt(recording, template, offset, amplitude) {
        const start = Math.max(0, offset);
        const end = Math.min(recording.length, offset + template.length);

        for (let i = start; i < end; i++) recording[i] += template[i - offset] * amplitude;

        return recording;
    },

    /** Run every device's recording through detection and return one report per device. */
    detectAll({ devices, template }, config) {
        const slotSamples = Math.round(config.slotSeconds * config.sampleRate);
        const searchSamples = Math.round(
            (config.maximumOutputLatencySeconds + config.chirp.durationSeconds + 0.05) * config.sampleRate
        );

        return devices.map((device, slot) => {
            const envelope = EchoDsp.matchedFilterEnvelope(device.recording, template);
            const detected = EchoDsp.detectSlotPeaks({
                envelope,
                slotCount: devices.length,
                slotSamples,
                ownSlot: slot,
                ownSearchStart: device.ownSearchStart,
                ownSearchSamples: searchSamples,
                peakOptions: config.peakOptions ?? {}
            });

            return {
                deviceId: `sim-${slot}`,
                slot,
                sampleRate: config.sampleRate,
                epsilon: config.epsilon[slot],
                peaks: (detected?.peaks ?? new Array(devices.length).fill(null)).map(peak => peak?.index ?? null)
            };
        });
    },

    /** Synthesize, detect and solve, reporting recovered geometry against the ground truth. */
    runRound(overrides = {}) {
        const config = this.buildConfiguration(overrides);
        const round = this.synthesizeRound(config);
        const reports = this.detectAll(round, config);
        const solved = EchoDsp.solveRound(reports, { speedOfSound: config.speedOfSound });

        return {
            config,
            reports,
            ...solved,
            distanceErrors: this.distanceErrors(solved.matrix, config),
            positionErrors: this.positionErrors(solved, config)
        };
    },

    distanceErrors(matrix, config) {
        const errors = [];

        for (let i = 0; i < matrix.length; i++)
            for (let j = i + 1; j < matrix.length; j++) {
                const truth = this.separation(config.positions[i], config.positions[j]);
                errors.push({
                    pair: [i, j],
                    truth,
                    measured: matrix[i][j],
                    error: matrix[i][j] === null ? null : matrix[i][j] - truth
                });
            }

        return errors;
    },

    positionErrors({ keep, points }, config) {
        if (points.length !== keep.length || points.length < 2) return [];

        const truth = keep.map(index => config.positions[index]);
        const aligned = EchoDsp.alignToReference(points, truth);
        return aligned.map((point, i) => this.separation(point, truth[i]));
    },

    worstDistanceError(result) {
        const magnitudes = result.distanceErrors.map(({ error }) => (error === null ? Infinity : Math.abs(error)));
        return magnitudes.length === 0 ? 0 : Math.max(...magnitudes);
    },

    worstPositionError(result) {
        return result.positionErrors.length === 0 ? Infinity : Math.max(...result.positionErrors);
    }
};

if (typeof window !== "undefined") window.EchoSim = EchoSim;
