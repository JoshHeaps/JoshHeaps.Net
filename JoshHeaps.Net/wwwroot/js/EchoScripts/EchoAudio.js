/*
 * Microphone capture and chirp playback, both indexed by AudioContext frame.
 *
 * Frames, not wall-clock: currentTime * sampleRate is exactly the frame number, so a playback
 * scheduled at an audio-clock time converts directly into a position in the recording. Nothing here
 * depends on a timer firing on schedule, which matters because a hidden tab's timers are throttled
 * to about once a second and its chirp would land in another device's slot.
 */
const EchoAudio = {
    TARGET_SAMPLE_RATE: 48000,
    RING_SECONDS: 20,
    BLUETOOTH_HINTS: /bluetooth|airpod|hands-?free|headset|\bbt\b|wireless/i,

    context: null,
    stream: null,
    capture: null,
    ring: null,
    highestFrame: 0,
    peakAmplitude: 0,
    warnings: [],
    onSamples: null,

    async start() {
        if (this.context) return this.describe();

        this.stream = await navigator.mediaDevices.getUserMedia({
            // Echo cancellation exists to delete sounds this device just played, which is exactly
            // the measurement. Gain control and noise suppression distort the chirp's envelope.
            audio: {
                echoCancellation: false,
                noiseSuppression: false,
                autoGainControl: false,
                channelCount: 1
            }
        });

        this.context = new AudioContext({ sampleRate: this.TARGET_SAMPLE_RATE, latencyHint: "interactive" });
        await this.context.audioWorklet.addModule("/js/EchoScripts/EchoCaptureProcessor.js");
        await this.context.resume();

        this.ring = new Float32Array(Math.ceil(this.context.sampleRate * this.RING_SECONDS));
        this.attachCapture();
        this.warnings = this.inspectDevice();

        return this.describe();
    },

    attachCapture() {
        const source = this.context.createMediaStreamSource(this.stream);
        this.capture = new AudioWorkletNode(this.context, "echo-capture", { numberOfOutputs: 0 });
        this.capture.port.onmessage = ({ data }) => this.write(data.frame, data.samples);
        source.connect(this.capture);
        return this.capture;
    },

    write(frame, samples) {
        const capacity = this.ring.length;
        let loudest = 0;

        for (let i = 0; i < samples.length; i++) {
            this.ring[(frame + i) % capacity] = samples[i];
            loudest = Math.max(loudest, Math.abs(samples[i]));
        }

        this.peakAmplitude = Math.max(this.peakAmplitude * 0.95, loudest);
        this.highestFrame = Math.max(this.highestFrame, frame + samples.length);
        this.onSamples?.(this.highestFrame);
        return this.highestFrame;
    },

    /** The recording position for an AudioContext time. Exact: currentTime * sampleRate is a frame. */
    frameAt(contextTime) {
        return Math.round(contextTime * this.context.sampleRate);
    },

    /** Copy an absolute frame range out of the ring, or null if it is not (or no longer) held. */
    read(startFrame, length) {
        if (length <= 0) return null;
        if (startFrame + length > this.highestFrame) return null;
        if (startFrame < this.highestFrame - this.ring.length) return null;

        const capacity = this.ring.length;
        const window = new Float32Array(length);
        for (let i = 0; i < length; i++) window[i] = this.ring[((startFrame + i) % capacity + capacity) % capacity];

        return window;
    },

    /**
     * Schedule a chirp on the audio clock and return the frame it was scheduled for. When it
     * actually leaves the speaker is later by an unknown output latency and does not need to be
     * known — it is recovered from the recording of it.
     */
    play(chirp, atContextTime, gain = 0.5) {
        const when = Math.max(atContextTime, this.context.currentTime);
        const buffer = this.context.createBuffer(1, chirp.length, this.context.sampleRate);
        buffer.copyToChannel(chirp, 0);

        const source = this.context.createBufferSource();
        const volume = this.context.createGain();
        volume.gain.value = gain;
        source.buffer = buffer;
        source.connect(volume).connect(this.context.destination);
        source.start(when);

        return this.frameAt(when);
    },

    /** How far behind the audio clock the main thread's view of the recording is running. */
    captureLagSeconds() {
        return this.context.currentTime - this.highestFrame / this.context.sampleRate;
    },

    /** Reported output latency, which bounds how late a scheduled chirp can reach the microphone. */
    outputLatencySeconds() {
        return (this.context.outputLatency || 0) + (this.context.baseLatency || 0);
    },

    inspectDevice() {
        const warnings = [];
        const track = this.stream.getAudioTracks()[0];
        const settings = track?.getSettings() ?? {};

        if (this.context.sampleRate !== this.TARGET_SAMPLE_RATE)
            warnings.push(`Running at ${this.context.sampleRate}Hz instead of 48000Hz — ranges stay correct but resolution drops.`);

        if (this.BLUETOOTH_HINTS.test(track?.label ?? ""))
            warnings.push("This looks like a Bluetooth microphone. Bluetooth resamples and re-times the stream, which breaks the measurement — switch to the built-in speaker and microphone.");

        for (const [name, label] of [["echoCancellation", "Echo cancellation"], ["autoGainControl", "Auto gain"], ["noiseSuppression", "Noise suppression"]])
            if (settings[name] === true) warnings.push(`${label} could not be turned off on this device — the measurement will be unreliable.`);

        return warnings;
    },

    describe() {
        const track = this.stream.getAudioTracks()[0];

        return {
            sampleRate: this.context.sampleRate,
            label: track?.label ?? "microphone",
            outputLatencyMs: Math.round(this.outputLatencySeconds() * 1000),
            warnings: this.warnings
        };
    },

    async stop() {
        this.stream?.getTracks().forEach(track => track.stop());
        await this.context?.close();
        this.context = null;
        this.stream = null;
        this.capture = null;
        this.ring = null;
        this.highestFrame = 0;
        this.onSamples = null;
        return true;
    }
};

if (typeof window !== "undefined") window.EchoAudio = EchoAudio;
