"use strict";
const test = require("node:test"),
  assert = require("node:assert/strict"),
  fs = require("node:fs"),
  path = require("node:path");
const A = require("./actions.js");
const G = require("./geometry.js");
globalThis.EagleActions = A;
const B = require("./bridge.js");
const catalog = JSON.parse(
  fs.readFileSync(path.join(__dirname, "../Assets/actions.json"), "utf8"),
);
const scene = JSON.parse(
  fs.readFileSync(
    path.join(__dirname, "../Assets/SceneProps/work-scenes.json"),
    "utf8",
  ),
).scenes[0];
const once = ["Yawn", "Shy", "Eat", "Tea", "Annoyed"];
test("eating uses two synchronized supporting wings instead of empty scooping", () => {
  for (let i = 0; i <= 480; i++) {
    const q = A.sample("Eat", i / 480).parameters;
    assert.equal(q.ParamWingLLift, q.ParamWingRLift);
    assert.equal(q.ParamWingLFold, q.ParamWingRFold);
  }
});
test("expressive reactions keep a second wing low instead of merging both palms", () => {
  for (const kind of ["Shy", "Annoyed"])
    for (let i = 0; i <= 480; i++) {
      const q = A.sample(kind, i / 480).parameters;
      assert.ok(Math.min(q.ParamWingLLift, q.ParamWingRLift) <= 0.15, kind);
    }
});
test("authored contact follows actual triangle deformation, not a nearest vertex", () => {
  const binding = G.bindPoint([0, 0, 10, 0, 0, 10], [0, 1, 2], [2, 3]);
  assert.deepEqual(G.point([0, 0, 20, 0, 0, 30], binding), { x: 4, y: 9 });
  assert.throws(() => G.bindPoint([0, 0, 10, 0, 0, 10], [0, 1, 2], [12, 12]));
});
test("held props keep their authored grip pinned while tilting", () => {
  const anchor = { x: 180, y: 220 },
    grip = [0.25, 0.72],
    rect = G.heldRect([50, 40], anchor, grip, 0.9);
  const c = G.corners(rect, 0.3, [anchor.x, anchor.y]);
  const x =
    c[0][0] + grip[0] * (c[2][0] - c[0][0]) + grip[1] * (c[1][0] - c[0][0]);
  const y =
    c[0][1] + grip[0] * (c[2][1] - c[0][1]) + grip[1] * (c[1][1] - c[0][1]);
  assert.ok(Math.abs(x - anchor.x) < 1e-9 && Math.abs(y - anchor.y) < 1e-9);
});
test("hanging hands cannot be copied above a desk they have not reached", () => {
  assert.equal(G.aboveDesk(286, 276), 0);
  assert.equal(G.aboveDesk(270, 276), 0);
  assert.equal(G.aboveDesk(250, 276), 1);
  assert.ok(G.aboveDesk(259, 276) > 0 && G.aboveDesk(259, 276) < 1);
});
test("T-T tear bands stay joined to eyelids with bounded length and seamless flow", () => {
  for (const side of ["l", "r"]) {
    const eye = { x: 150, y: 159 },
      a = G.tear(eye, 0.3, side),
      b = G.tear({ x: 157, y: 154 }, 0.3, side);
    assert.equal(b.rect[0] - a.rect[0], 7);
    assert.equal(b.rect[1] - a.rect[1], -5);
    assert.equal(a.rect[2], 12);
    assert.equal(a.opacity, 1);
    assert.equal(a.rect[1], eye.y);
    assert.ok(a.rect[3] >= 22.5 && a.rect[3] <= 25.5);
    assert.equal(G.tear(eye, 0.3, side, 0).rect[3], 0);
    const first = G.tear(eye, 0, side),
      last = G.tear(eye, 1, side);
    first.rect.forEach((value, i) =>
      assert.ok(Math.abs(value - last.rect[i]) < 1e-9),
    );
  }
});
test("hungry T-T keeps both eyes closed and grows tears only after closure", () => {
  for (let i = 0; i <= 480; i++) {
    const p = i / 480,
      loop = A.sample("HungryLoop", p),
      enter = A.sample("HungryEnter", p);
    assert.equal(loop.parameters.ParamEyeLOpen, 0);
    assert.equal(loop.parameters.ParamEyeROpen, 0);
    if (enter.props.tears > 0) assert.equal(enter.parameters.ParamEyeLOpen, 0);
  }
  assert.equal(A.sample("HungryExit", 0.2).parameters.ParamEyeLOpen, 0);
  assert.equal(A.sample("HungryExit", 0.27).props.tears, 0);
});
test("a paused transition retains its elapsed blend time and resumes continuously", () => {
  const clock = { startedAt: 100 };
  assert.equal(B.transitionProgress(clock, 130, false), 0.25);
  assert.equal(B.transitionProgress(clock, 130, true), 0.25);
  assert.equal(B.transitionProgress(clock, 9130, true), 0.25);
  assert.equal(B.transitionProgress(clock, 9130, false), 0.25);
  assert.equal(B.transitionProgress(clock, 9160, false), 0.5);
});
test("neutral handoff rejects stale requests and preserves identity across pause updates", () => {
  const request = {
    type: "neutralize",
    protocol: 1,
    generation: 1,
    requestId: 3,
    revision: 10,
    sequence: 7,
    kind: "Eat",
    paused: false,
    equipment: {
      desk: "desk.default",
      computer: "computer.default",
      outfit: "outfit.default",
    },
  };
  const accepted = B.neutralize(request, 1, null, 9);
  assert.notEqual(accepted.equipment, request.equipment);
  assert.equal(
    B.neutralize({ ...request, paused: true }, 1, accepted, 10).paused,
    true,
  );
  assert.equal(
    B.neutralize({ ...request, requestId: 2 }, 1, accepted, 10),
    null,
  );
  assert.equal(B.neutralize(request, 1, accepted, 11), null);
  assert.throws(() =>
    B.neutralize({ ...request, sequence: 8 }, 1, accepted, 10),
  );
  assert.throws(() =>
    B.neutralize({ ...request, kind: "WorkLoop" }, 1, null, 9),
  );
  assert.throws(() => B.neutralize(request, 2, null, 9));
});
test("all fourteen catalog actions and Idle have exact original durations", () => {
  assert.equal(Object.keys(A.durations).length, 15);
  for (const item of catalog.actions)
    assert.equal(A.durations[item.clip], item.durationSeconds, item.clip);
});
test("one-shot actions begin and finish in the same real neutral geometry", () => {
  for (const kind of once)
    for (const p of [0, 1])
      for (const [id, value] of Object.entries(A.sample(kind, p).parameters))
        assert.ok(value === A.neutral()[id], kind + " " + p + " " + id);
});
test("scene phase boundaries share poses without neutral detours", () => {
  for (const [from, to] of [
    ["WorkEnter", "WorkLoop"],
    ["WorkLoop", "WorkToBusy"],
    ["WorkToBusy", "BusyLoop"],
    ["BusyLoop", "BusyExit"],
    ["WorkLoop", "WorkExit"],
    ["HungryEnter", "HungryLoop"],
    ["HungryLoop", "HungryExit"],
  ]) {
    const a = A.sample(from, 1).parameters,
      b = A.sample(to, 0).parameters;
    for (const id of Object.keys(a))
      assert.ok(Math.abs(a[id] - b[id]) < 1e-9, from + " -> " + to + " " + id);
  }
});
test("loop endpoints and exits stay continuous", () => {
  for (const kind of ["WorkLoop", "BusyLoop", "HungryLoop", "Idle"]) {
    const a = A.sample(kind, 0).parameters,
      b = A.sample(kind, 1).parameters;
    for (const id of Object.keys(a))
      assert.ok(Math.abs(a[id] - b[id]) < 1e-9, kind + " " + id);
  }
  for (const kind of ["WorkExit", "BusyExit", "HungryExit"])
    assert.deepEqual(A.sample(kind, 1).parameters, A.neutral(), kind);
});
test("every authored parameter remains finite and in its model range at 240 Hz", () => {
  for (const kind of Object.keys(A.durations))
    for (let i = 0; i <= 960; i++) {
      const sample = A.sample(kind, i / 960);
      for (const [id, value] of Object.entries(sample.parameters)) {
        assert.ok(Number.isFinite(value), kind + id);
        const bound = id.startsWith("ParamAngle")
          ? 45
          : id.startsWith("ParamBodyAngle")
            ? 10
            : 1;
        assert.ok(
          value >= -bound && value <= bound,
          kind + " " + id + "=" + value,
        );
      }
    }
});
test("geometry actions interpolate locally, no finite parameter jumps", () => {
  for (const kind of Object.keys(A.durations))
    for (let i = 0; i < 1000; i++) {
      const p = i / 1000,
        a = A.sample(kind, p).parameters,
        b = A.sample(kind, p + 1e-7).parameters;
      for (const id of Object.keys(a))
        if (id !== "ParamBusyEyes")
          assert.ok(Math.abs(a[id] - b[id]) < 0.001, kind + " " + id);
    }
});
test("table arrives before computer landing and no layer order swaps", () => {
  const m = scene.motion,
    landing =
      m.computerEnter.startSeconds +
      m.computerEnter.durationSeconds * m.dropLandingFraction;
  assert.ok(A.scene("WorkEnter", landing / 3.5, scene).deskX === 0);
  assert.deepEqual(scene.layerOrder, [
    "fire",
    "character-body",
    "desk-back",
    "character-foreground",
    "desk-front",
    "computer",
  ]);
});
test("scene props are fully outside at exact terminal and full loop stationary", () => {
  for (const kind of ["WorkExit", "BusyExit"]) {
    const p = A.scene(kind, 1, scene);
    assert.equal(p.deskX, -scene.motion.deskTravelPixels);
    assert.equal(p.computerY, -scene.motion.computerTravelPixels);
    assert.equal(p.fire, 0);
  }
  assert.equal(A.scene("WorkLoop", 0.5, scene).deskX, 0);
  assert.equal(A.scene("HungryLoop", 0.5, scene).computer, false);
});
const message = (overrides = {}) => ({
  type: "state",
  protocol: 1,
  generation: "g",
  revision: 2,
  sequence: 1,
  kind: "Yawn",
  phase: "Playing",
  progress: 0.25,
  durationSeconds: 2,
  paused: false,
  equipment: {
    desk: "desk.default",
    computer: "computer.default",
    outfit: "outfit.default",
  },
  ...overrides,
});
test("bridge rejects stale generation, malformed terminal and altered duration", () => {
  assert.throws(() => B.state(message(), "other"));
  assert.throws(() => B.state(message({ phase: "Terminal" }), "g"));
  assert.throws(() => B.state(message({ durationSeconds: 5 }), "g"));
  assert.equal(B.state(message(), "g", { revision: 2 }), null);
});
test("local sampling is bounded and frozen on pause or authoritative terminal", () => {
  assert.equal(B.progress(message(), 100, 600), 0.5);
  assert.equal(B.progress(message(), 100, 9000), 1);
  assert.equal(B.progress(message({ paused: true }), 100, 9000), 0.25);
  assert.equal(
    B.progress(message({ phase: "Terminal", progress: 1 }), 100, 9000),
    1,
  );
});
test("equipment snapshots are copied, not writable through host object reference", () => {
  const original = message(),
    copy = B.state(original, "g");
  original.equipment.desk = "changed";
  assert.equal(copy.equipment.desk, "desk.default");
});
test("100ms idle snapshots do not reset breathing or blink time", () => {
  let previous = message({
      kind: "Idle",
      phase: "Idle",
      durationSeconds: 0,
      progress: 0,
    }),
    at = 0;
  for (let i = 1; i <= 15; i++) {
    previous = B.align(
      message({ kind: "Idle", phase: "Idle", durationSeconds: 0, progress: 0 }),
      previous,
      at,
      i * 100,
    );
    at = i * 100;
  }
  assert.ok(Math.abs(previous.progress - 0.375) < 1e-9);
});
test("same sequence snapshots never rewind locally displayed progress", () => {
  assert.equal(
    B.align(message({ progress: 0.26 }), message(), 100, 400).progress,
    0.4,
  );
  assert.equal(
    B.align(message({ sequence: 2, progress: 0 }), message(), 100, 400)
      .progress,
    0,
  );
});
test("actual timeline phase names are accepted", () => {
  for (const phase of ["Playing", "ReturningToIdle", "Paused"])
    assert.equal(B.state(message({ phase }), "g").phase, phase);
});
test("black pupil and yellow busy eyes exchange only behind fully closed lids", () => {
  for (const [kind, cut] of [
    ["WorkToBusy", 0.46],
    ["BusyExit", 0.32],
  ]) {
    for (const p of [cut - 0.03, cut - 0.001, cut, cut + 0.001, cut + 0.03]) {
      const q = A.sample(kind, p).parameters;
      assert.equal(q.ParamEyeLOpen, 0);
      assert.equal(q.ParamEyeROpen, 0);
      assert.ok(q.ParamBusyEyes === 0 || q.ParamBusyEyes === 1);
    }
  }
  assert.equal(A.sample("BusyLoop", 0.5).parameters.ParamBusyEyes, 1);
  assert.equal(A.sample("BusyLoop", 0.5).parameters.ParamEyeLOpen, 1);
});
