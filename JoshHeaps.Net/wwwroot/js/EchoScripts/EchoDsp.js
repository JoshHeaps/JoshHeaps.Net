/*
 * Pure signal-processing and geometry for acoustic ranging. No DOM, no Web Audio, no network:
 * everything here is a function of its arguments so the simulator and the tests can drive the
 * exact code the page runs.
 */
const EchoDsp = {
    speedOfSound(temperatureCelsius = 20) {
        return 331.3 + 0.606 * temperatureCelsius;
    },

    nextPowerOfTwo(value) {
        let size = 1;
        while (size < value) size <<= 1;
        return size;
    },

    /**
     * Linear frequency sweep, tapered at both ends. A sweep is used rather than a tone because a
     * tone's autocorrelation peaks once per period, leaving no unambiguous arrival to measure.
     */
    makeChirp({ sampleRate, durationSeconds, startHz, endHz, taperFraction = 0.15 }) {
        const length = Math.round(sampleRate * durationSeconds);
        const sweepRate = (endHz - startHz) / durationSeconds;
        const chirp = new Float32Array(length);

        for (let i = 0; i < length; i++) {
            const t = i / sampleRate;
            chirp[i] = Math.sin(2 * Math.PI * (startHz * t + 0.5 * sweepRate * t * t));
        }

        return this.applyTaper(chirp, taperFraction);
    },

    applyTaper(signal, fraction) {
        const edge = Math.max(1, Math.floor(signal.length * fraction));

        for (let i = 0; i < edge; i++) {
            const window = 0.5 - 0.5 * Math.cos((Math.PI * i) / edge);
            signal[i] *= window;
            signal[signal.length - 1 - i] *= window;
        }

        return signal;
    },

    twiddles(size) {
        this._twiddleCache ??= new Map();
        const cached = this._twiddleCache.get(size);
        if (cached) return cached;

        const half = size >> 1;
        const table = { cos: new Float64Array(half), sin: new Float64Array(half) };

        for (let i = 0; i < half; i++) {
            const angle = (-2 * Math.PI * i) / size;
            table.cos[i] = Math.cos(angle);
            table.sin[i] = Math.sin(angle);
        }

        this._twiddleCache.set(size, table);
        return table;
    },

    fft(real, imaginary, inverse = false) {
        const size = real.length;
        this.reverseBits(real, imaginary);
        const { cos, sin } = this.twiddles(size);

        for (let span = 2; span <= size; span <<= 1) {
            const half = span >> 1;
            const stride = size / span;

            for (let base = 0; base < size; base += span) {
                for (let k = 0; k < half; k++) {
                    const twiddle = k * stride;
                    const wReal = cos[twiddle];
                    const wImaginary = inverse ? -sin[twiddle] : sin[twiddle];

                    const lower = base + k;
                    const upper = lower + half;
                    const productReal = real[upper] * wReal - imaginary[upper] * wImaginary;
                    const productImaginary = real[upper] * wImaginary + imaginary[upper] * wReal;

                    real[upper] = real[lower] - productReal;
                    imaginary[upper] = imaginary[lower] - productImaginary;
                    real[lower] += productReal;
                    imaginary[lower] += productImaginary;
                }
            }
        }

        if (!inverse) return { real, imaginary };

        for (let i = 0; i < size; i++) {
            real[i] /= size;
            imaginary[i] /= size;
        }

        return { real, imaginary };
    },

    reverseBits(real, imaginary) {
        const size = real.length;

        for (let i = 1, j = 0; i < size; i++) {
            let bit = size >> 1;
            for (; j & bit; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i >= j) continue;

            [real[i], real[j]] = [real[j], real[i]];
            [imaginary[i], imaginary[j]] = [imaginary[j], imaginary[i]];
        }

        return { real, imaginary };
    },

    /**
     * Matched-filter envelope of a recording against a template, via FFT cross-correlation.
     * Negative frequencies are dropped so the result is the analytic envelope rather than a burst
     * oscillating at the sweep frequency — an oscillating peak defeats sub-sample interpolation
     * and makes first-arrival detection jitter by half a carrier period.
     */
    matchedFilterEnvelope(recording, template) {
        const size = this.nextPowerOfTwo(recording.length + template.length);
        const recordingReal = new Float64Array(size);
        const recordingImaginary = new Float64Array(size);
        const templateReal = new Float64Array(size);
        const templateImaginary = new Float64Array(size);

        recordingReal.set(recording);
        templateReal.set(template);
        this.fft(recordingReal, recordingImaginary);
        this.fft(templateReal, templateImaginary);

        const analyticReal = new Float64Array(size);
        const analyticImaginary = new Float64Array(size);
        const half = size >> 1;

        for (let i = 0; i <= half; i++) {
            const gain = i === 0 || i === half ? 1 : 2;
            analyticReal[i] = gain * (recordingReal[i] * templateReal[i] + recordingImaginary[i] * templateImaginary[i]);
            analyticImaginary[i] = gain * (recordingImaginary[i] * templateReal[i] - recordingReal[i] * templateImaginary[i]);
        }

        this.fft(analyticReal, analyticImaginary, true);

        const envelope = new Float32Array(recording.length);
        for (let i = 0; i < envelope.length; i++)
            envelope[i] = Math.sqrt(analyticReal[i] * analyticReal[i] + analyticImaginary[i] * analyticImaginary[i]);

        return envelope;
    },

    maxInRange(values, start, end) {
        let index = start;
        let value = -Infinity;

        for (let i = start; i < end; i++) {
            if (values[i] <= value) continue;
            value = values[i];
            index = i;
        }

        return { index, value };
    },

    medianInRange(values, start, end, sampleLimit = 2048) {
        const span = end - start;
        if (span <= 0) return 0;

        const stride = Math.max(1, Math.floor(span / sampleLimit));
        const sampled = [];
        for (let i = start; i < end; i += stride) sampled.push(values[i]);

        sampled.sort((left, right) => left - right);
        return sampled[sampled.length >> 1];
    },

    /**
     * Sub-sample peak position by fitting a parabola through the peak and its neighbours. One
     * tenth of a sample is 0.7mm of range at 48kHz, so this is most of the accuracy for free.
     */
    refinePeakIndex(envelope, index) {
        if (index <= 0 || index >= envelope.length - 1) return index;

        const before = envelope[index - 1];
        const peak = envelope[index];
        const after = envelope[index + 1];
        const curvature = before - 2 * peak + after;
        if (curvature === 0) return index;

        const offset = (0.5 * (before - after)) / curvature;
        return index + Math.max(-1, Math.min(1, offset));
    },

    /**
     * First arrival in a window, not the loudest one. A reflection off a wall or table often
     * comes back louder than the direct path, and only the direct path is the distance.
     */
    findFirstPeak(envelope, options = {}) {
        const start = Math.max(0, Math.floor(options.start ?? 0));
        const end = Math.min(envelope.length, Math.ceil(options.end ?? envelope.length));
        if (end - start < 8) return null;

        const loudest = this.maxInRange(envelope, start, end);
        const noiseFloor = this.medianInRange(envelope, start, end);
        const snr = noiseFloor > 0 ? loudest.value / noiseFloor : Infinity;
        if (snr < (options.minSnr ?? 4)) return null;

        // Held above the noise floor so sidelobes cannot trigger it, but well below the loudest
        // arrival so that a reflection several times louder than the direct path cannot mask it.
        const threshold = Math.max(
            noiseFloor * (options.noiseMultiple ?? 6),
            loudest.value * (options.relativeThreshold ?? 0.15)
        );

        let crossing = start;
        while (crossing < end && envelope[crossing] < threshold) crossing++;
        if (crossing >= end) return null;

        const lobeEnd = Math.min(end, crossing + (options.lobeSamples ?? 64));
        const arrival = this.maxInRange(envelope, crossing, lobeEnd);

        return { index: this.refinePeakIndex(envelope, arrival.index), amplitude: arrival.value, snr };
    },

    /**
     * Locate every chirp of one round in a single device's recording.
     *
     * The device's own chirp is the anchor: it is always present and always the loudest thing in
     * the recording, and its position absorbs this device's own output and input latency. Every
     * other slot is then searched relative to that anchor, so no clock agreement between devices
     * is required — only that the chirps stay in their slots.
     */
    detectSlotPeaks({
        envelope,
        slotCount,
        slotSamples,
        ownSlot,
        ownSearchStart,
        ownSearchSamples,
        slotHints = null,
        peakOptions = {}
    }) {
        const own = this.findFirstPeak(envelope, {
            ...peakOptions,
            start: ownSearchStart,
            end: ownSearchStart + ownSearchSamples
        });

        if (!own) return null;

        const anchor = own.index - ownSlot * slotSamples;
        const pad = Math.floor(slotSamples * 0.45);
        const peaks = new Array(slotCount).fill(null);
        peaks[ownSlot] = own;

        for (let slot = 0; slot < slotCount; slot++) {
            if (slot === ownSlot) continue;

            const centre = anchor + slot * slotSamples + (slotHints?.[slot] ?? 0);
            peaks[slot] = this.findFirstPeak(envelope, {
                ...peakOptions,
                start: centre - pad,
                end: centre + pad
            });
        }

        return { anchor, peaks };
    },

    /**
     * Distance between two devices from four arrival indices, each measured inside the recording
     * of the device that made it. Clock offset and audio-pipeline latency appear once with each
     * sign and cancel; the devices' own speaker-to-microphone spacing does not, and is added back.
     */
    pairDistance({ a1, a2, b1, b2, sampleRate, sampleRateA, sampleRateB, speedOfSound, epsilonA = 0, epsilonB = 0 }) {
        // Each interval is converted to seconds in its own device's sample rate before the two are
        // subtracted: a device that hands back 44100 instead of 48000 would otherwise contribute
        // its interval in the wrong unit.
        const intervalA = (a2 - a1) / (sampleRateA ?? sampleRate);
        const intervalB = (b2 - b1) / (sampleRateB ?? sampleRate);
        return ((intervalA - intervalB) / 2) * speedOfSound + (epsilonA + epsilonB) / 2;
    },

    /**
     * Symmetric distance matrix from one round of reports. Entries stay null where either device
     * failed to hear one of the four chirps the pair needs.
     */
    buildDistanceMatrix(reports, { speedOfSound = 343, maxDistance = 40 } = {}) {
        const count = reports.length;
        const matrix = Array.from({ length: count }, () => new Array(count).fill(null));

        for (let i = 0; i < count; i++) {
            matrix[i][i] = 0;

            for (let j = i + 1; j < count; j++) {
                const distance = this.distanceBetween(reports[i], reports[j], speedOfSound);
                if (distance === null || distance < -1 || distance > maxDistance) continue;

                matrix[i][j] = Math.max(0, distance);
                matrix[j][i] = matrix[i][j];
            }
        }

        return matrix;
    },

    distanceBetween(deviceA, deviceB, speedOfSound) {
        const a1 = deviceA.peaks[deviceA.slot];
        const a2 = deviceA.peaks[deviceB.slot];
        const b1 = deviceB.peaks[deviceA.slot];
        const b2 = deviceB.peaks[deviceB.slot];
        if (a1 === null || a2 === null || b1 === null || b2 === null) return null;

        return this.pairDistance({
            a1,
            a2,
            b1,
            b2,
            sampleRateA: deviceA.sampleRate,
            sampleRateB: deviceB.sampleRate,
            speedOfSound,
            epsilonA: deviceA.epsilon ?? 0,
            epsilonB: deviceB.epsilon ?? 0
        });
    },

    /**
     * Largest set of devices linked by measured distances. Anything outside it cannot be placed
     * relative to the others, and leaving it in makes the completed matrix infinite.
     */
    largestConnectedComponent(matrix) {
        const unvisited = new Set(matrix.map((_, index) => index));
        let largest = [];

        while (unvisited.size > 0) {
            const component = [];
            const queue = [unvisited.values().next().value];
            unvisited.delete(queue[0]);

            while (queue.length > 0) {
                const current = queue.pop();
                component.push(current);

                for (const next of unvisited)
                    if (matrix[current][next] !== null) {
                        unvisited.delete(next);
                        queue.push(next);
                    }
            }

            if (component.length > largest.length) largest = component;
        }

        return largest.sort((left, right) => left - right);
    },

    /**
     * Drop devices whose distances are geometrically impossible. One device reporting a bad peak
     * distorts the whole layout, so the worst triangle-inequality offender is removed and the
     * check repeated. Below four devices there is no redundancy left and nothing can be checked.
     */
    rejectOutliers(matrix, candidates, tolerance = 0.5) {
        const keep = [...candidates];

        while (keep.length > 3) {
            const violations = this.countTriangleViolations(matrix, keep, tolerance);
            const worst = violations.reduce((best, count, index) => (count > violations[best] ? index : best), 0);
            if (violations[worst] === 0) break;

            keep.splice(worst, 1);
        }

        return keep;
    },

    submatrix(matrix, indices) {
        return indices.map(row => indices.map(column => matrix[row][column]));
    },

    countTriangleViolations(matrix, keep, tolerance) {
        const violations = new Array(keep.length).fill(0);

        for (let i = 0; i < keep.length; i++) {
            for (let j = i + 1; j < keep.length; j++) {
                for (let k = j + 1; k < keep.length; k++) {
                    const sides = [matrix[keep[i]][keep[j]], matrix[keep[j]][keep[k]], matrix[keep[i]][keep[k]]];
                    if (sides.some(side => side === null)) continue;

                    const longest = Math.max(...sides);
                    const perimeter = sides.reduce((sum, side) => sum + side, 0);
                    if (longest <= perimeter - longest + tolerance) continue;

                    violations[i]++;
                    violations[j]++;
                    violations[k]++;
                }
            }
        }

        return violations;
    },

    /** Fill gaps with the shortest known path between the two devices so MDS gets a full matrix. */
    completeMatrix(matrix) {
        const count = matrix.length;
        const filled = matrix.map(row => row.map(value => (value === null ? Infinity : value)));

        for (let via = 0; via < count; via++)
            for (let i = 0; i < count; i++)
                for (let j = 0; j < count; j++)
                    filled[i][j] = Math.min(filled[i][j], filled[i][via] + filled[via][j]);

        return filled;
    },

    /** Jacobi eigendecomposition of a symmetric matrix. Returns eigenvalues and column vectors. */
    symmetricEigen(matrix, maxSweeps = 100, tolerance = 1e-14) {
        const count = matrix.length;
        const working = matrix.map(row => Float64Array.from(row));
        const vectors = Array.from({ length: count }, (_, i) => {
            const column = new Float64Array(count);
            column[i] = 1;
            return column;
        });

        for (let sweep = 0; sweep < maxSweeps; sweep++) {
            if (this.offDiagonalMagnitude(working) < tolerance) break;

            for (let p = 0; p < count - 1; p++)
                for (let q = p + 1; q < count; q++)
                    this.rotateOut(working, vectors, p, q);
        }

        return {
            values: working.map((row, i) => row[i]),
            vectors
        };
    },

    offDiagonalMagnitude(matrix) {
        let total = 0;

        for (let i = 0; i < matrix.length; i++)
            for (let j = i + 1; j < matrix.length; j++) total += matrix[i][j] * matrix[i][j];

        return total;
    },

    rotateOut(matrix, vectors, p, q) {
        if (Math.abs(matrix[p][q]) < 1e-300) return matrix;

        const theta = (matrix[q][q] - matrix[p][p]) / (2 * matrix[p][q]);
        const sign = theta >= 0 ? 1 : -1;
        const tangent = sign / (Math.abs(theta) + Math.sqrt(theta * theta + 1));
        const cosine = 1 / Math.sqrt(tangent * tangent + 1);
        const sine = tangent * cosine;
        const count = matrix.length;

        for (let k = 0; k < count; k++) {
            const left = matrix[k][p];
            const right = matrix[k][q];
            matrix[k][p] = cosine * left - sine * right;
            matrix[k][q] = sine * left + cosine * right;
        }

        for (let k = 0; k < count; k++) {
            const left = matrix[p][k];
            const right = matrix[q][k];
            matrix[p][k] = cosine * left - sine * right;
            matrix[q][k] = sine * left + cosine * right;
        }

        for (let k = 0; k < count; k++) {
            const left = vectors[k][p];
            const right = vectors[k][q];
            vectors[k][p] = cosine * left - sine * right;
            vectors[k][q] = sine * left + cosine * right;
        }

        return matrix;
    },

    /**
     * Classical multidimensional scaling: coordinates whose pairwise distances best reproduce the
     * matrix. The result is only defined up to rotation, translation and mirroring.
     */
    classicalMds(distances, dimensions = 2) {
        const count = distances.length;
        const squared = distances.map(row => row.map(value => value * value));
        const rowMeans = squared.map(row => row.reduce((sum, value) => sum + value, 0) / count);
        const grandMean = rowMeans.reduce((sum, value) => sum + value, 0) / count;

        const centred = squared.map((row, i) => row.map((value, j) => -0.5 * (value - rowMeans[i] - rowMeans[j] + grandMean)));
        const { values, vectors } = this.symmetricEigen(centred);
        const order = values
            .map((value, index) => ({ value, index }))
            .sort((left, right) => right.value - left.value)
            .slice(0, dimensions);

        return Array.from({ length: count }, (_, i) =>
            order.map(({ value, index }) => vectors[i][index] * Math.sqrt(Math.max(0, value)))
        );
    },

    /**
     * Rotate, mirror and translate a constellation onto a reference layout. Without this, every
     * solve returns an arbitrary orientation and the display spins and flips between updates.
     */
    alignToReference(points, reference) {
        if (!reference || reference.length !== points.length || points.length === 0) return points;

        const pointCentre = this.centroid(points);
        const referenceCentre = this.centroid(reference);
        let best = null;

        for (const mirror of [1, -1]) {
            const candidate = this.rotateOnto(points, reference, pointCentre, referenceCentre, mirror);
            if (!best || candidate.residual < best.residual) best = candidate;
        }

        return best.points;
    },

    centroid(points) {
        const sum = points.reduce((total, [x, y]) => [total[0] + x, total[1] + y], [0, 0]);
        return [sum[0] / points.length, sum[1] / points.length];
    },

    rotateOnto(points, reference, pointCentre, referenceCentre, mirror) {
        let sineTerm = 0;
        let cosineTerm = 0;

        for (let i = 0; i < points.length; i++) {
            const px = (points[i][0] - pointCentre[0]) * mirror;
            const py = points[i][1] - pointCentre[1];
            const qx = reference[i][0] - referenceCentre[0];
            const qy = reference[i][1] - referenceCentre[1];
            sineTerm += px * qy - py * qx;
            cosineTerm += px * qx + py * qy;
        }

        const angle = Math.atan2(sineTerm, cosineTerm);
        const cosine = Math.cos(angle);
        const sine = Math.sin(angle);
        let residual = 0;

        const aligned = points.map((point, i) => {
            const px = (point[0] - pointCentre[0]) * mirror;
            const py = point[1] - pointCentre[1];
            const x = px * cosine - py * sine + referenceCentre[0];
            const y = px * sine + py * cosine + referenceCentre[1];
            residual += (x - reference[i][0]) ** 2 + (y - reference[i][1]) ** 2;
            return [x, y];
        });

        return { points: aligned, residual };
    },

    /**
     * Full solve for one round: distances, connectivity, outlier rejection, then a constellation
     * aligned onto the previous frame.
     *
     * <c>previousPoints</c> is indexed by report position, with null for devices that were dropped
     * last round, so alignment survives devices coming and going.
     */
    solveRound(reports, { speedOfSound = 343, previousPoints = null, tolerance = 0.5 } = {}) {
        const matrix = this.buildDistanceMatrix(reports, { speedOfSound });
        const connected = this.largestConnectedComponent(matrix);
        const keep = this.rejectOutliers(matrix, connected, tolerance);
        const points = keep.length >= 2 ? this.classicalMds(this.completeMatrix(this.submatrix(matrix, keep))) : [];
        const reference = previousPoints ? keep.map(index => previousPoints[index]) : null;
        const alignable = reference?.length === points.length && reference.every(Boolean);

        return {
            matrix,
            keep,
            points: alignable ? this.alignToReference(points, reference) : points
        };
    }
};

if (typeof window !== "undefined") window.EchoDsp = EchoDsp;
