import test from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";

const require = createRequire(import.meta.url);
const L = require("./loafing.js");
const A = require("./actions.js");
const busy = A.sample("BusyLoop", 0.35).parameters;
const message = (revision, phase = "throw", seconds = 0, paused = false) => ({
  type: "loafing", protocol: 1, generation: 7, revision,
  phase, seconds, paused, kind: "NosePicking",
});
const near = (actual, expected, tolerance = 1e-10) =>
  assert.ok(Math.abs(actual - expected) < tolerance, `${actual} != ${expected}`);

test("throw and return each last 2.8 seconds; desk never moves or fades", () => {
  assert.equal(L.duration, 2.8);
  for (const phase of ["throw", "loop", "return"])
    for (let i = 0; i <= 168; i++) {
      const sample = L.sample(phase, i / 60, busy);
      assert.equal(sample.deskX, 0);
      assert.equal("alpha" in sample, false);
      for (const value of Object.values(sample.parameters)) assert.ok(Number.isFinite(value));
    }
});
test("computer leaves through upper right before the eagle hides", () => {
  const start = L.sample("throw", 0.25, busy);
  assert.equal(start.computerX, 0);
  assert.equal(start.computerY, 0);
  let previousX = 0;
  for (let s = 0.25; s < 1.15; s += 1 / 60) {
    const frame = L.sample("throw", s, busy);
    assert.ok(frame.computerX >= previousX);
    assert.ok(frame.computerY <= 0);
    assert.equal(frame.modelOffsetY, 0);
    previousX = frame.computerX;
  }
  const hidden = L.sample("throw", 1.95, busy);
  assert.equal(hidden.modelVisible, false);
  assert.equal(hidden.computerVisible, false);
  assert.equal(hidden.modelOffsetY, 300);
});
test("loop has only a fixed desk, no Core actor, laptop, fire or gaze pose", () => {
  const frame = L.sample("loop", 1800, busy);
  assert.equal(frame.active, true);
  assert.equal(frame.modelVisible, false);
  assert.equal(frame.computerVisible, false);
  assert.equal(frame.fire, 0);
  assert.equal(frame.deskX, 0);
});
test("return restores unchanged work pose and computer at the exact endpoint", () => {
  assert.equal(L.sample("return", 0.8, busy).modelVisible, false);
  assert.equal(L.sample("return", 1.4, busy).computerVisible, false);
  assert.equal(L.sample("return", 1.6, busy).modelOffsetY, 0);
  const end = L.sample("return", 2.8, busy);
  for (const [key, value] of Object.entries(busy)) near(end.parameters[key], value);
  assert.equal(end.computerX, 0);
  near(end.computerY, 0);
  assert.equal(Math.abs(end.computerRotation), 0);
  assert.equal(end.modelOffsetY, 0);
  assert.equal(end.modelVisible, true);
});
test("busy pupil style switches only behind closed eyes", () => {
  for (const [phase, boundary] of [["throw", 0.15], ["return", 2.48]]) {
    const before = L.sample(phase, boundary - 0.00001, busy).parameters;
    const after = L.sample(phase, boundary, busy).parameters;
    assert.notEqual(before.ParamBusyEyes, after.ParamBusyEyes);
    assert.ok(before.ParamEyeLOpen < 1e-6);
    assert.ok(before.ParamEyeROpen < 1e-6);
    assert.equal(after.ParamEyeLOpen, 0);
    assert.equal(after.ParamEyeROpen, 0);
  }
});
test("the independent clock advances smoothly between host samples and clamps", () => {
  const clock = new L.Tracking(7);
  assert.equal(clock.receive(message(1), 100, busy), true);
  near(clock.sample(600).seconds, 0.5);
  clock.receive(message(2, "throw", 0.48), 600, {});
  near(clock.sample(700).seconds, 0.6);
  near(clock.sample(10000).seconds, 2.8);
  assert.equal(clock.sample(700).parameters.ParamBusyEyes, 0);
});
test("pause freezes stage time; repeated paused samples do not restart it", () => {
  const clock = new L.Tracking(7);
  clock.receive(message(1), 0, busy);
  clock.receive(message(2, "throw", 0.5, true), 500);
  const frame = clock.sample(500);
  assert.deepEqual(clock.sample(6500), frame);
  clock.receive(message(3, "throw", 0.5, true), 6500);
  near(clock.sample(12000).seconds, 0.5);
  clock.receive(message(4, "throw", 0.5), 12000);
  near(clock.sample(12100).seconds, 0.6);
});
test("old revisions cannot replay a prior phase or choose a new kind", () => {
  const clock = new L.Tracking(7);
  clock.receive(message(5, "loop", 30), 0, busy);
  assert.equal(clock.receive(message(4), 50), false);
  assert.equal(clock.sample(100).phase, "loop");
  assert.equal(clock.sample(100).kind, "NosePicking");
  clock.receive({ ...message(6, "none"), kind: "None" }, 100);
  assert.equal(clock.sample(100).active, false);
  clock.receive({ ...message(7), kind: "WoodenFish" }, 200, busy);
  assert.equal(clock.sample(200).seconds, 0);
  assert.equal(clock.sample(200).kind, "WoodenFish");
});
test("invalid protocol, generation, time and action names are rejected", () => {
  for (const override of [{ protocol: 2 }, { generation: 9 }, { revision: NaN },
    { seconds: -1 }, { seconds: Infinity }, { paused: 1 }, { phase: "fade" },
    { kind: "None" }, { kind: "Injected" }]) {
    const clock = new L.Tracking(7);
    assert.throws(() => clock.receive({ ...message(1), ...override }, 0), /Invalid local loafing/);
    assert.equal(clock.revision, -1);
  }
});
test("stage uses post-render FBO cropping and never acknowledges a hidden terminal", () => {
  const source = readFileSync(new URL("./stage.js", import.meta.url), "utf8");
  assert.match(source, /model\.draw\("all", this\.framebuffer, offsetY\)/);
  assert.match(source, /quad\.draw\([\s\S]*?\{ id: this\.image \}[\s\S]*?undefined,\s*true/);
  assert.match(source, /!loaf\.active\s*&&\s*current\.phase === "Terminal"/);
  assert.match(source, /!neutralizing\s*&&\s*!loaf\.active\s*&&\s*!!m\.gazeRanges/);
  assert.match(source, /loafing: loafing\.sample/);
});
