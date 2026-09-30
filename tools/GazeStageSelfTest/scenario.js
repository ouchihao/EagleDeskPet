"use strict";
(async () => {
  const stage = window.eagleStage;
  const equipment = {
    desk: "desk.default",
    computer: "computer.default",
    outfit: "outfit.default",
  };
  let revision = 100,
    sequence = 100,
    gazeRevision = 100;
  const snapshots = [],
    checks = [],
    messages = [];
  const originalPost = chrome.webview.postMessage.bind(chrome.webview);
  chrome.webview.postMessage = (message) => {
    messages.push(message);
    originalPost(message);
  };
  const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  function check(value, name) {
    checks.push({ name, passed: !!value });
    if (!value) throw new Error(name);
  }
  function gaze(x, y, enabled = true) {
    return stage.receive({
      type: "gaze",
      protocol: 1,
      generation: 1,
      revision: ++gazeRevision,
      enabled,
      x,
      y,
    });
  }
  function state(kind = "Idle", paused = false) {
    return stage.receive({
      type: "state",
      protocol: 1,
      generation: 1,
      revision: ++revision,
      sequence: ++sequence,
      kind,
      phase: kind === "Idle" ? "Idle" : "Playing",
      progress: 0,
      durationSeconds: kind === "Idle" ? 0 : kind === "WorkEnter" ? 3.5 : 2,
      paused,
      equipment,
    });
  }
  function capture(name) {
    const report = stage.report();
    snapshots.push({ name, ...report });
    originalPost({ type: "gaze-qa-frame", name, data: stage.capture() });
    return report;
  }
  async function hold(x, y, ms = 700, enabled = true) {
    for (let time = 0; time < ms; time += 50) {
      await gaze(x, y, enabled);
      await wait(50);
    }
  }
  try {
    await state();
    await wait(180);
    check(
      stage.report().gaze.supported,
      "Active model declares real head and pupil tracking",
    );
    stage.beginRecording(30);
    capture("idle-neutral");
    for (const [name, x, y] of [
      ["left", -1, 0],
      ["right", 1, 0],
      ["up", 0, 1],
      ["down", 0, -1],
      ["up-left", -1, 1],
      ["down-right", 1, -1],
    ]) {
      await hold(x, y);
      const r = capture("gaze-" + name);
      check(
        Math.abs(r.gaze.eyeX - x) < 0.03 && Math.abs(r.gaze.eyeY - y) < 0.03,
        name + " eye converges without sign reversal",
      );
    }
    await hold(0, 0, 1500, false);
    let r = capture("gaze-leave-neutral");
    check(
      Math.abs(r.gaze.headX) < 0.005 && Math.abs(r.gaze.headY) < 0.005,
      "Far pointer smoothly returns to neutral",
    );
    await hold(1, 1);
    await state("Idle", true);
    await hold(-1, -1, 1500);
    r = capture("paused-neutral");
    check(
      Math.abs(r.gaze.headX) < 0.005 && Math.abs(r.gaze.eyeX) < 0.005,
      "Paused idle ignores new pointer and releases gaze",
    );
    await state();
    await hold(1, 0);
    await wait(1600);
    r = capture("stale-input-neutral");
    check(Math.abs(r.gaze.headX) < 0.01, "Missing host samples release gaze");
    for (const kind of ["Shy", "WorkEnter"]) {
      await state();
      await hold(-1, 1);
      await state(kind);
      capture(kind + "-entry");
      await wait(60);
      capture(kind + "-transition");
      await hold(1, -1, 250);
      r = capture(kind + "-authored");
      check(
        r.gaze.headX === 0 && r.gaze.eyeX === 0,
        kind + " does not apply gaze to authored motion",
      );
    }
    await state();
    await hold(1, -1);
    await stage.receive({
      type: "neutralize",
      protocol: 1,
      generation: 1,
      requestId: 1,
      revision: ++revision,
      sequence: ++sequence,
      kind: "Yawn",
      equipment,
      paused: false,
    });
    capture("Yawn-handoff-entry");
    await wait(60);
    capture("Yawn-handoff-transition");
    await hold(-1, 1, 250);
    r = capture("Yawn-handoff-neutral");
    check(
      messages.filter((m) => m.type === "neutralized").length === 1,
      "PNG Yawn receives one rendered neutral handoff acknowledgement",
    );
    check(
      Object.entries(r.parameters).every(
        ([id, value]) =>
          Math.abs(
            value - (id === "ParamEyeLOpen" || id === "ParamEyeROpen" ? 1 : 0),
          ) < 1e-8,
      ),
      "PNG handoff has no residual head or pupil offset",
    );
    await state();
    await hold(0, 1);
    capture("return-idle-up");
    if (window.gazeQaAllOutfits) {
      check(
        stage.capabilities.outfits.length === 6,
        "All six distinct outfit models are present",
      );
      for (const outfit of stage.capabilities.outfits) {
        equipment.outfit = outfit;
        await stage.receive({
          type: "prepare",
          protocol: 1,
          generation: 1,
          requestId: ++revision,
          equipment,
        });
        await state();
        await wait(180);
        check(
          stage.report().gaze.supported,
          outfit + " declares real gaze binding",
        );
        for (const [name, x, y] of [
          ["left", -1, 0],
          ["right", 1, 0],
          ["up", 0, 1],
          ["down", 0, -1],
        ]) {
          await hold(x, y);
          const frame = capture(outfit.replaceAll(".", "-") + "-" + name);
          check(
            Math.abs(frame.gaze.eyeX - x) < 0.03 &&
              Math.abs(frame.gaze.eyeY - y) < 0.03,
            outfit + " " + name + " converges in actual Stage",
          );
        }
      }
    }
    const report = stage.report();
    check(
      report.errors.length === 0 &&
        report.glErrors.length === 0 &&
        report.nonFiniteVertices === 0,
      "No runtime, GL or nonfinite vertices",
    );
    check(
      report.maximumFootDelta === 0,
      "Feet stay exactly anchored in Core geometry",
    );
    const video = await stage.finishRecording();
    window.gazeQaResult = {
      passed: true,
      checks,
      snapshots,
      report,
      video,
      allOutfitsTested: !!window.gazeQaAllOutfits,
      limitations: [
        "Isolated actual production Stage, not native desktop mouse injection or physical Present measurement",
        "PNG neutral handoff is exercised; the WPF PNG overlay itself is not displayed by this tool",
        "Default candidate override does not validate remaining outfit models",
      ],
    };
  } catch (error) {
    window.gazeQaResult = {
      passed: false,
      error: String(error.stack || error),
      checks,
      snapshots,
      report: stage.report(),
    };
  }
})();
