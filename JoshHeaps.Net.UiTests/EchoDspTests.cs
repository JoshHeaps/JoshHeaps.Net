using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace JoshHeaps.Net.UiTests;

/// <summary>
/// Exercises the acoustic-ranging pipeline against a simulated room. The DSP runs in the browser
/// because that is where it runs in production; these tests drive the same files the page loads,
/// so there is no second implementation to drift.
/// </summary>
[TestFixture]
public class EchoDspTests : PageTest
{
    private TestConfiguration Config => TestConfiguration.Instance;

    public override BrowserNewContextOptions ContextOptions() => Config.GetBrowserContextOptions();

    [SetUp]
    public async Task LoadPipeline()
    {
        await Page.GotoAsync(Config.Test.BaseUrl);
        await Page.AddScriptTagAsync(new() { Url = "/js/EchoScripts/EchoDsp.js" });
        await Page.AddScriptTagAsync(new() { Url = "/js/EchoScripts/EchoSim.js" });
    }

    [Test]
    public async Task Matched_Filter_Finds_The_Chirp_Within_One_Sample()
    {
        var error = await Page.EvaluateAsync<double>("""
            () => {
                const sampleRate = 48000;
                const chirp = EchoDsp.makeChirp({ sampleRate, durationSeconds: 0.05, startHz: 2000, endHz: 8000 });
                const recording = new Float32Array(sampleRate);
                for (let i = 0; i < recording.length; i++) recording[i] = (Math.random() * 2 - 1) * 0.02;

                const offset = 12345;
                for (let i = 0; i < chirp.length; i++) recording[offset + i] += chirp[i] * 0.3;

                const peak = EchoDsp.findFirstPeak(EchoDsp.matchedFilterEnvelope(recording, chirp));
                return Math.abs(peak.index - offset);
            }
            """);

        Assert.That(error, Is.LessThan(1.0), "arrival should be located to within a sample");
    }

    [Test]
    public async Task A_Tone_Cannot_Be_Located_But_A_Chirp_Can()
    {
        var ratios = await Page.EvaluateAsync<double[]>("""
            () => {
                const sampleRate = 48000;
                const sidelobeRatio = template => {
                    const recording = new Float32Array(sampleRate / 2);
                    const offset = 8000;
                    for (let i = 0; i < template.length; i++) recording[offset + i] += template[i];

                    const envelope = EchoDsp.matchedFilterEnvelope(recording, template);
                    const peak = EchoDsp.maxInRange(envelope, 0, envelope.length);
                    let highest = 0;
                    for (let i = 0; i < envelope.length; i++) {
                        if (Math.abs(i - peak.index) < 200) continue;
                        highest = Math.max(highest, envelope[i]);
                    }
                    return highest / peak.value;
                };

                const chirp = EchoDsp.makeChirp({ sampleRate, durationSeconds: 0.05, startHz: 2000, endHz: 8000 });
                const tone = EchoDsp.makeChirp({ sampleRate, durationSeconds: 0.05, startHz: 5000, endHz: 5000 });
                return [sidelobeRatio(chirp), sidelobeRatio(tone)];
            }
            """);

        Assert.That(ratios[1], Is.GreaterThan(0.5), "a tone should correlate almost as well far from the true arrival");
        Assert.That(ratios[0], Is.LessThan(0.25), "a chirp should give one unambiguous arrival");
    }

    [Test]
    public async Task Clock_Offset_And_Pipeline_Latency_Cancel()
    {
        var distance = await Page.EvaluateAsync<double>("""
            () => {
                const sampleRate = 48000;
                const speedOfSound = 343;
                const flight = (4.2 / speedOfSound) * sampleRate;
                const slot = 0.4 * sampleRate;
                const offsetB = 987654;
                const latencyA = 0.031 * sampleRate;
                const latencyB = 0.128 * sampleRate;

                return EchoDsp.pairDistance({
                    a1: latencyA,
                    a2: slot + latencyB + flight,
                    b1: offsetB + latencyA + flight,
                    b2: offsetB + slot + latencyB,
                    sampleRate,
                    speedOfSound
                });
            }
            """);

        Assert.That(distance, Is.EqualTo(4.2).Within(0.001));
    }

    [Test]
    public async Task Collocated_Devices_Read_Zero_Before_Calibration()
    {
        var distance = await Page.EvaluateAsync<double>("""
            () => {
                const sampleRate = 48000;
                const speedOfSound = 343;
                const spacing = (0.19 / speedOfSound) * sampleRate;
                const slot = 0.4 * sampleRate;
                const latencyA = 0.04 * sampleRate;
                const latencyB = 0.11 * sampleRate;

                // Two tabs on one machine: one speaker, one microphone, so the self path and the
                // cross path are the same physical distance.
                return EchoDsp.pairDistance({
                    a1: latencyA + spacing,
                    a2: slot + latencyB + spacing,
                    b1: latencyA + spacing,
                    b2: slot + latencyB + spacing,
                    sampleRate,
                    speedOfSound
                });
            }
            """);

        Assert.That(distance, Is.EqualTo(0).Within(0.001));
    }

    [Test]
    public async Task Speaker_To_Microphone_Spacing_Is_Added_Back()
    {
        var distances = await Page.EvaluateAsync<double[]>("""
            () => {
                const sampleRate = 48000;
                const speedOfSound = 343;
                const truth = 3.0;
                const epsilonA = 0.18;
                const epsilonB = 0.04;
                const samples = metres => (metres / speedOfSound) * sampleRate;
                const slot = 0.4 * sampleRate;

                const peaks = {
                    a1: samples(epsilonA),
                    a2: slot + samples(truth),
                    b1: samples(truth),
                    b2: slot + samples(epsilonB),
                    sampleRate,
                    speedOfSound
                };

                return [
                    EchoDsp.pairDistance(peaks),
                    EchoDsp.pairDistance({ ...peaks, epsilonA, epsilonB })
                ];
            }
            """);

        Assert.That(distances[0], Is.EqualTo(3.0 - 0.11).Within(0.005), "uncorrected range reads short");
        Assert.That(distances[1], Is.EqualTo(3.0).Within(0.005), "correcting for spacing recovers the true range");
    }

    [Test]
    public async Task Simulated_Room_Recovers_Distances_And_Layout()
    {
        var errors = await Page.EvaluateAsync<double[]>("""
            () => {
                const result = EchoSim.runRound({
                    positions: [[0, 0], [3.2, 0], [3.0, 2.6], [0.4, 2.9], [1.7, 1.4]]
                });
                return [EchoSim.worstDistanceError(result), EchoSim.worstPositionError(result), result.keep.length];
            }
            """);

        Assert.That(errors[2], Is.EqualTo(5), "every device should survive a clean round");
        Assert.That(errors[0], Is.LessThan(0.05), "worst pairwise range error");
        Assert.That(errors[1], Is.LessThan(0.15), "worst recovered position error");
    }

    [Test]
    public async Task A_Reflection_Louder_Than_The_Direct_Path_Does_Not_Win()
    {
        var errors = await Page.EvaluateAsync<double[]>("""
            () => {
                const measure = relativeThreshold => EchoSim.worstDistanceError(EchoSim.runRound({
                    positions: [[0, 0], [3.4, 0], [2.9, 2.7], [0.2, 2.5]],
                    reflections: [{ extraMetres: 1.8, gain: 5 }],
                    peakOptions: { relativeThreshold }
                }));

                return [measure(undefined), measure(0.5)];
            }
            """);

        Assert.That(errors[0], Is.LessThan(0.05), "the first arrival is the distance, not the loudest one");
        Assert.That(errors[1], Is.GreaterThan(1.5),
            "a threshold high enough to miss the direct path must measure the reflection instead — this is what the default guards against");
    }

    [Test]
    public async Task Echoes_Inside_The_Correlation_Lobe_Bound_The_Accuracy()
    {
        var errors = await Page.EvaluateAsync<double[]>("""
            () => {
                const positions = [[0, 0], [3.2, 0], [3.0, 2.6], [0.4, 2.9], [1.7, 1.4]];
                const worst = reflectionExtraRange =>
                    EchoSim.worstDistanceError(EchoSim.runRound({ positions, reflectionExtraRange }));

                return [worst([0.4, 4.0]), worst([0.08, 0.4])];
            }
            """);

        Assert.That(errors[0], Is.LessThan(0.01), "echoes well clear of the direct arrival are rejected outright");
        Assert.That(errors[1], Is.LessThan(0.15),
            "echoes arriving inside the correlation lobe cannot be separated and bias the range — this bounds what a device resting on a hard surface can achieve");
    }

    [Test]
    public async Task A_Bad_Measurement_Is_Rejected_And_The_Layout_Survives()
    {
        var outcome = await Page.EvaluateAsync<double[]>("""
            () => {
                const positions = [[0, 0], [3.2, 0], [3.0, 2.6], [0.4, 2.9], [1.6, 1.3]];
                const config = EchoSim.buildConfiguration({ positions });
                const reports = EchoSim.detectAll(EchoSim.synthesizeRound(config), config);

                reports[3].peaks[0] += 9000;

                const solved = EchoDsp.solveRound(reports, { speedOfSound: config.speedOfSound });
                const truth = solved.keep.map(index => positions[index]);
                const aligned = EchoDsp.alignToReference(solved.points, truth);
                const worst = Math.max(...aligned.map((point, i) => EchoSim.separation(point, truth[i])));
                const brokenPairSurvived = solved.keep.includes(0) && solved.keep.includes(3);

                return [solved.keep.length, brokenPairSurvived ? 1 : 0, worst];
            }
            """);

        Assert.That(outcome[0], Is.EqualTo(4), "exactly one endpoint of the bad pair should be dropped");
        Assert.That(outcome[1], Is.EqualTo(0), "the impossible pair must not survive");
        Assert.That(outcome[2], Is.LessThan(0.2), "the remaining layout should be unpoisoned");
    }

    [Test]
    public async Task Alignment_Undoes_An_Arbitrary_Rotation_And_Mirror()
    {
        var errors = await Page.EvaluateAsync<double[]>("""
            () => {
                const reference = [[0, 0], [3.4, 0], [2.9, 2.7], [0.2, 2.5]];
                const scramble = (points, angle, mirror) => points.map(([x, y]) => {
                    const mx = x * mirror;
                    return [mx * Math.cos(angle) - y * Math.sin(angle) + 11, mx * Math.sin(angle) + y * Math.cos(angle) - 4];
                });

                const worst = mirror => {
                    const aligned = EchoDsp.alignToReference(scramble(reference, 0.9, mirror), reference);
                    return Math.max(...aligned.map((point, i) => EchoSim.separation(point, reference[i])));
                };

                return [worst(1), worst(-1)];
            }
            """);

        Assert.That(errors[0], Is.LessThan(1e-9), "rotation and translation should be recovered exactly");
        Assert.That(errors[1], Is.LessThan(1e-9), "a mirrored solve should be un-mirrored onto the reference");
    }

    [Test]
    public async Task Consecutive_Frames_Do_Not_Rotate_Or_Flip()
    {
        var drift = await Page.EvaluateAsync<double>("""
            () => {
                const positions = [[0, 0], [3.2, 0], [3.0, 2.6], [0.4, 2.9]];
                const first = EchoSim.runRound({ positions, seed: 11 });

                const previous = new Array(positions.length).fill(null);
                first.keep.forEach((device, i) => { previous[device] = first.points[i]; });

                const config = EchoSim.buildConfiguration({ positions, seed: 22 });
                const reports = EchoSim.detectAll(EchoSim.synthesizeRound(config), config);
                const second = EchoDsp.solveRound(reports, {
                    speedOfSound: config.speedOfSound,
                    previousPoints: previous
                });

                return Math.max(...second.keep.map((device, i) => EchoSim.separation(second.points[i], previous[device])));
            }
            """);

        Assert.That(drift, Is.LessThan(0.3), "a stationary room should not move between frames");
    }
}
