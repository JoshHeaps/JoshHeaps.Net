/*
 * Capture worklet. Every block is tagged with the AudioContext frame it started at, which makes the
 * context's own clock the index for all captured audio: a scheduled playback time converts to a
 * recording position exactly, with no dependence on when the main thread got round to noticing.
 *
 * A worklet rather than ScriptProcessorNode because a dropped buffer would break the continuity
 * that the sample count depends on.
 */
const BLOCK_SAMPLES = 2048;

class EchoCaptureProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.block = new Float32Array(BLOCK_SAMPLES);
        this.filled = 0;
        this.blockStartFrame = 0;
    }

    process(inputs) {
        const channel = inputs[0]?.[0];
        if (!channel) return true;

        for (let i = 0; i < channel.length; i++) {
            if (this.filled === 0) this.blockStartFrame = currentFrame + i;
            this.block[this.filled++] = channel[i];
            if (this.filled === BLOCK_SAMPLES) this.flush();
        }

        return true;
    }

    flush() {
        const samples = this.block.slice(0, this.filled);
        this.port.postMessage({ frame: this.blockStartFrame, samples }, [samples.buffer]);
        this.filled = 0;
    }
}

registerProcessor("echo-capture", EchoCaptureProcessor);
