(function () {
    "use strict";

    // ===== Utilities =====
    function $(id) { return document.getElementById(id); }

    // Deterministic PRNG (mulberry32)
    function mulberry32(seed) {
        let t = (seed >>> 0) || 1;
        return function () {
            t += 0x6D2B79F5;
            let r = Math.imul(t ^ (t >>> 15), 1 | t);
            r ^= r + Math.imul(r ^ (r >>> 7), 61 | r);
            return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
        };
    }

    // Colors + palette (R, G, B, Y, C, M)
    const COLOR_NAMES = ["R", "G", "B", "Y", "C", "M"];
    const PALETTE = [
        "#ff0000", // R
        "#00ff00", // G
        "#0066ff", // B
        "#ffff00", // Y
        "#00ffff", // C
        "#ff00ff"  // M
    ];
    const COLOR_LABELS = ["Red", "Green", "Blue", "Yellow", "Cyan", "Magenta"];

    // Build a 6x6 interaction matrix in [-1, +1]
    function buildRules(rand) {
        const n = COLOR_NAMES.length;
        const rules = Array.from({ length: n }, () => Array(n).fill(0));
        for (let i = 0; i < n; i++) {
            for (let j = 0; j < n; j++) {
                if (i === j) {
                    // self interaction: mild cohesion/dispersion
                    rules[i][j] = (rand() * 2 - 1) * 0.6; // [-0.6, 0.6]
                } else {
                    // cross interaction: wider range
                    rules[i][j] = (rand() * 2 - 1);       // [-1, 1]
                }
            }
        }
        // stability pass: cap per-row energy
        for (let i = 0; i < n; i++) {
            const s = rules[i].reduce((a, b) => a + Math.abs(b), 0);
            if (s > 3) {
                const k = 3 / s;
                for (let j = 0; j < n; j++) rules[i][j] *= k;
            }
        }
        return rules;
    }

    // ===== Canvas helpers =====
    function prepareContext(canvas) {
        const ctx = canvas.getContext("2d", { alpha: false });
        resizeToDisplay(canvas, ctx);
        return ctx;
    }

    function resizeToDisplay(canvas, ctx) {
        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();
        const w = Math.max(1, Math.floor(rect.width * dpr));
        const h = Math.max(1, Math.floor(rect.height * dpr));
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width = w; canvas.height = h;
        }
        // Scale so drawing uses CSS pixels
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    // ===== Physics constants =====
    const SOFTEN = 25;      // px^2 in denominator
    const MAX_ACC = 1.5;    // acceleration clamp (px/s^2)
    const DAMP = 0.985;     // velocity damping per step
    const RANGE = 240;      // interaction cutoff (px); 0 to disable
    const RANGE2 = RANGE * RANGE;
    const RADIUS = 2;       // render radius
    const RULE_MIN = -1, RULE_MAX = 1;

    // Collisions
    const COLLISION_RADIUS = RADIUS;        // collide when centers < 2*R
    const COLLISION_DIAM = COLLISION_RADIUS * 2;
    const COLLISION_ITER = 1;               // solver iterations per substep (raise to 2 for tighter packing)
    const REST = 0.2;                       // coefficient of restitution [0..1]
    const FRICTION = 0.02;                  // tangential friction

    // ===== Typed arrays (SoA) =====
    let px, py, vx, vy, col; // Float32Array, Float32Array, Float32Array, Float32Array, Uint8Array
    let N = 0;

    function createParticlesTyped(rand, count, width, height) {
        px = new Float32Array(count);
        py = new Float32Array(count);
        vx = new Float32Array(count);
        vy = new Float32Array(count);
        col = new Uint8Array(count);
        for (let i = 0; i < count; i++) {
            px[i] = rand() * width;
            py[i] = rand() * height;
            vx[i] = (rand() - 0.5) * 0.1;
            vy[i] = (rand() - 0.5) * 0.1;
            col[i] = i % COLOR_NAMES.length; // round-robin for even distribution
        }
        N = count;
    }

    // ===== Spatial grid (uniform grid / spatial hash) =====
    const CELL_SIZE = Math.max(RANGE, COLLISION_DIAM * 2); // must cover interaction/collision radius
    let GRID_W = 0, GRID_H = 0, GRID = []; // array of arrays of indices

    function rebuildGrid(W, H) {
        GRID_W = Math.ceil(W / CELL_SIZE) | 0;
        GRID_H = Math.ceil(H / CELL_SIZE) | 0;
        const cells = GRID_W * GRID_H;

        if (GRID.length !== cells) GRID = Array.from({ length: cells }, () => []);
        else for (let i = 0; i < cells; i++) GRID[i].length = 0;

        for (let i = 0; i < N; i++) {
            let cx = (Math.floor(px[i] / CELL_SIZE) % GRID_W + GRID_W) % GRID_W;
            let cy = (Math.floor(py[i] / CELL_SIZE) % GRID_H + GRID_H) % GRID_H;
            GRID[cy * GRID_W + cx].push(i);
        }
    }

    function forEachNeighborIndices(cx, cy, fn) {
        for (let dy = -1; dy <= 1; dy++) {
            for (let dx = -1; dx <= 1; dx++) {
                const nx = (cx + dx + GRID_W) % GRID_W;
                const ny = (cy + dy + GRID_H) % GRID_H;
                const cell = GRID[ny * GRID_W + nx];
                for (let k = 0; k < cell.length; k++) fn(cell[k]);
            }
        }
    }

    // ===== Physics step (typed arrays + grid) =====
    function step(dt, speed, W, H, rules) {
        const halfW = W * 0.5, halfH = H * 0.5;

        for (let i = 0; i < N; i++) {
            let ax = 0, ay = 0;

            const cx = (Math.floor(px[i] / CELL_SIZE) % GRID_W + GRID_W) % GRID_W;
            const cy = (Math.floor(py[i] / CELL_SIZE) % GRID_H + GRID_H) % GRID_H;

            forEachNeighborIndices(cx, cy, (j) => {
                if (j === i) return;

                let dx = px[j] - px[i];
                let dy = py[j] - py[i];

                // toroidal shortest vector
                if (dx > halfW) dx -= W; else if (dx < -halfW) dx += W;
                if (dy > halfH) dy -= H; else if (dy < -halfH) dy += H;

                const d2 = dx * dx + dy * dy;
                if (RANGE && d2 > RANGE2) return;

                const k = rules[col[i]][col[j]];
                const inv = 1 / (d2 + SOFTEN);
                ax += k * dx * inv;
                ay += k * dy * inv;
            });

            // clamp acceleration
            const a2 = ax * ax + ay * ay;
            if (a2 > MAX_ACC * MAX_ACC) {
                const s = MAX_ACC / Math.sqrt(a2);
                ax *= s; ay *= s;
            }

            // integrate
            vx[i] = (vx[i] + ax * dt * speed) * DAMP;
            vy[i] = (vy[i] + ay * dt * speed) * DAMP;
            px[i] += vx[i] * dt * speed;
            py[i] += vy[i] * dt * speed;

            // wrap
            if (px[i] < 0) px[i] += W; else if (px[i] >= W) px[i] -= W;
            if (py[i] < 0) py[i] += H; else if (py[i] >= H) py[i] -= H;
        }
    }

    // ===== Collision resolver (typed arrays + grid) =====
    function resolveCollisions(W, H) {
        const halfW = W * 0.5, halfH = H * 0.5;
        const minDist = COLLISION_DIAM;
        const minDist2 = minDist * minDist;

        for (let i = 0; i < N; i++) {
            const cx = (Math.floor(px[i] / CELL_SIZE) % GRID_W + GRID_W) % GRID_W;
            const cy = (Math.floor(py[i] / CELL_SIZE) % GRID_H + GRID_H) % GRID_H;

            forEachNeighborIndices(cx, cy, (j) => {
                if (j <= i) return;

                let dx = px[j] - px[i];
                let dy = py[j] - py[i];
                if (dx > halfW) dx -= W; else if (dx < -halfW) dx += W;
                if (dy > halfH) dy -= H; else if (dy < -halfH) dy += H;

                const d2 = dx * dx + dy * dy;
                if (d2 >= minDist2 || d2 === 0) return;

                const dist = Math.sqrt(d2);
                const nx = dx / dist, ny = dy / dist;
                const overlap = (minDist - dist) * 0.5;

                // separate
                px[i] -= nx * overlap; py[i] -= ny * overlap;
                px[j] += nx * overlap; py[j] += ny * overlap;

                // wrap after correction
                if (px[i] < 0) px[i] += W; else if (px[i] >= W) px[i] -= W;
                if (py[i] < 0) py[i] += H; else if (py[i] >= H) py[i] -= H;
                if (px[j] < 0) px[j] += W; else if (px[j] >= W) px[j] -= W;
                if (py[j] < 0) py[j] += H; else if (py[j] >= H) py[j] -= H;

                // velocity impulse
                const rvx = vx[j] - vx[i];
                const rvy = vy[j] - vy[i];
                const vrel = rvx * nx + rvy * ny;
                if (vrel < 0) {
                    const jimp = -(1 + REST) * vrel * 0.5; // equal mass
                    const jx = jimp * nx, jy = jimp * ny;
                    vx[i] -= jx; vy[i] -= jy;
                    vx[j] += jx; vy[j] += jy;

                    // tangential friction
                    const tvx = rvx - vrel * nx;
                    const tvy = rvy - vrel * ny;
                    const tmag = Math.hypot(tvx, tvy);
                    if (tmag > 1e-8) {
                        const tx = tvx / tmag, ty = tvy / tmag;
                        const fj = FRICTION * tmag * 0.5;
                        vx[i] += tx * fj; vy[i] += ty * fj;
                        vx[j] -= tx * fj; vy[j] -= ty * fj;
                    }
                }
            });
        }
    }

    // ===== Render (typed arrays) =====
    function render(ctx, W, H) {
        // fade to black
        ctx.globalAlpha = 0.1; // lower alpha = longer trails
        ctx.fillStyle = "#000";
        ctx.fillRect(0, 0, W, H);
        ctx.globalAlpha = 1;

        for (let i = 0; i < N; i++) {
            ctx.fillStyle = PALETTE[col[i]];
            ctx.beginPath();
            ctx.arc(px[i], py[i], RADIUS, 0, Math.PI * 2);
            ctx.fill();
        }
    }

    // ===== Main bootstrap =====
    function main() {
        const boot = window.__PARTICLE_BOOT__ || { seed: 123, n: 600, speed: 1 };

        const canvas = $("sim");
        const ctx = prepareContext(canvas);

        const seedInput = $("seed");
        const countInput = $("count");
        const speedRange = $("speed");
        const speedOut = $("speedOut");

        const restartBtn = $("restart");
        const permalinkBtn = $("permalink");
        const randomSeedBtn = $("randomSeed");

        // === Rule editor UI ===
        const activeColorSel = $("ruleActiveColor");
        const sliderIds = ["R", "G", "B", "Y", "C", "M"]; // targets
        const sliders = sliderIds.map(id => ({
            input: $("rule_" + id),
            out: $("rule_" + id + "_out")
        }));
        const copyRulesBtn = $("copyRules");
        const toggleImportBtn = $("toggleImport");
        const importArea = $("importArea");
        const rulesJson = $("rulesJson");
        const applyRulesBtn = $("applyRules");
        const cancelImportBtn = $("cancelImport");

        const toolbarEl = document.querySelector(".toolbar");
        const toggleToolbarBtn = document.getElementById("toggleToolbar");

        const mq = window.matchMedia("(max-width: 640px)");
        function applyToolbarMode() {
            if (mq.matches) {
                toolbarEl.classList.add("mobile");
                // In mobile, default closed as FAB
                toolbarEl.classList.remove("open", "collapsed");
            } else {
                toolbarEl.classList.remove("mobile", "open");
                // Desktop keeps your collapsed/expanded behavior
            }
        }
        mq.addEventListener("change", applyToolbarMode);
        applyToolbarMode();

        toggleToolbarBtn.addEventListener("click", () => {
            if (toolbarEl.classList.contains("mobile")) {
                toolbarEl.classList.toggle("open");           // mobile: toggle drawer
            } else {
                toolbarEl.classList.toggle("collapsed");      // desktop: toggle bar
            }
        });


        // Reflect initial values
        seedInput.value = String(boot.seed >>> 0);
        countInput.value = String(boot.n);
        speedRange.value = String(boot.speed);
        speedOut.textContent = speedRange.value;

        // State
        let rand, rules;
        let W = canvas.clientWidth;
        let H = canvas.clientHeight;

        // Fixed-step accumulator
        const DT = 1 / 60; // seconds
        let acc = 0;
        let last = performance.now();
        let running = true;

        function init(seed, n) {
            rand = mulberry32(seed >>> 0);
            rules = buildRules(rand);
            W = canvas.clientWidth;
            H = canvas.clientHeight;
            createParticlesTyped(rand, n, W, H);
            // sync UI with current active row
            syncRuleUI();
        }

        function frame(now) {
            if (!running) return;
            // keep canvas sized & transformed
            resizeToDisplay(canvas, ctx);
            W = canvas.clientWidth;
            H = canvas.clientHeight;

            const speed = +speedRange.value || 1;

            acc += Math.min(0.1, (now - last) / 1000);
            last = now;

            while (acc >= DT) {
                rebuildGrid(W, H);                 // build grid for forces
                step(DT, speed, W, H, rules);
                rebuildGrid(W, H);                 // positions changed; rebuild for collisions
                for (let it = 0; it < COLLISION_ITER; it++) resolveCollisions(W, H);
                acc -= DT;
            }

            render(ctx, W, H);
            requestAnimationFrame(frame);
        }

        // UI
        speedRange.addEventListener("input", () => {
            speedOut.textContent = speedRange.value;
        });

        function fmt(v) { return (v >= 0 ? "+" : "") + v.toFixed(2); }

        function syncRuleUI() {
            if (!rules) return;
            const src = parseInt(activeColorSel.value, 10) || 0;
            for (let t = 0; t < sliders.length; t++) {
                const val = Math.max(RULE_MIN, Math.min(RULE_MAX, rules[src][t] ?? 0));
                sliders[t].input.value = String(val);
                sliders[t].out.textContent = fmt(val);
            }
        }

        // Slider change -> update rules matrix live
        sliders.forEach((s, tIdx) => {
            const color = PALETTE[tIdx];
            s.input.style.setProperty("--slider-thumb", color);

            s.input.addEventListener("input", () => {
                const src = parseInt(activeColorSel.value, 10) || 0;
                const v = Math.max(RULE_MIN, Math.min(RULE_MAX, parseFloat(s.input.value)));
                rules[src][tIdx] = v;
                s.out.textContent = fmt(v);
            });
        });

        activeColorSel.addEventListener("change", syncRuleUI);

        copyRulesBtn.addEventListener("click", async () => {
            try {
                await navigator.clipboard.writeText(JSON.stringify(rules));
                copyRulesBtn.textContent = "Copied!";
                setTimeout(() => copyRulesBtn.textContent = "Copy Rules", 800);
            } catch { /* ignore */ }
        });

        toggleImportBtn.addEventListener("click", () => {
            importArea.hidden = !importArea.hidden;
        });

        cancelImportBtn.addEventListener("click", () => {
            importArea.hidden = true;
            rulesJson.value = "";
        });

        applyRulesBtn.addEventListener("click", () => {
            try {
                const obj = JSON.parse(rulesJson.value);
                if (!Array.isArray(obj) || obj.length !== 6 || !obj.every(row => Array.isArray(row) && row.length === 6)) throw new Error("Shape 6x6 required");
                // clamp values and assign
                for (let i = 0; i < 6; i++) for (let j = 0; j < 6; j++) {
                    const v = +obj[i][j];
                    if (!Number.isFinite(v)) throw new Error("Non-numeric rule");
                    obj[i][j] = Math.max(RULE_MIN, Math.min(RULE_MAX, v));
                }
                rules = obj;
                syncRuleUI();
                importArea.hidden = true;
            } catch (e) {
                alert("Invalid rules JSON: " + e.message);
            }
        });

        // end rules UI

        restartBtn.addEventListener("click", () => {
            const seed = (seedInput.value === "") ? (boot.seed >>> 0) : (parseInt(seedInput.value, 10) >>> 0);
            const n = Math.max(50, Math.min(5000, parseInt(countInput.value, 10) || boot.n));
            init(seed, n);
        });

        // ensure rule UI matches after manual seed change without clicking restart
        seedInput.addEventListener("change", () => { /* no-op until restart */ });

        permalinkBtn.addEventListener("click", async () => {
            const url = new URL(location.href);
            url.searchParams.set("seed", String(seedInput.value || boot.seed));
            url.searchParams.set("n", String(countInput.value || boot.n));
            url.searchParams.set("speed", String(speedRange.value || boot.speed));
            history.replaceState({}, "", url);
            try {
                await navigator.clipboard.writeText(url.toString());
                permalinkBtn.textContent = "Copied!";
                setTimeout(() => permalinkBtn.textContent = "Permalink", 800);
            } catch { /* ignore */ }
        });

        randomSeedBtn.addEventListener("click", () => {
            // cryptographically strong uint32
            const buf = new Uint32Array(1);
            (window.crypto || window.msCrypto).getRandomValues(buf);
            seedInput.value = String(buf[0] >>> 0);
        });

        // Pause on tab hide (saves battery)
        document.addEventListener("visibilitychange", () => {
            running = document.visibilityState !== "hidden";
            if (running) {
                last = performance.now();
                requestAnimationFrame(frame);
            }
        });

        // Initial start
        init(boot.seed >>> 0, boot.n);
        requestAnimationFrame(frame);

        // Handle window resizes
        window.addEventListener("resize", () => {
            resizeToDisplay(canvas, ctx);
        });
    }

    // Start when DOM is ready
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", main, { once: true });
    } else {
        main();
    }
})();