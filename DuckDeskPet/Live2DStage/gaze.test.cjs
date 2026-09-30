"use strict";
const test = require("node:test"),
  assert = require("node:assert/strict"),
  fs = require("node:fs"),
  path = require("node:path");
const G = require("./gaze.js"),
  A = require("./actions.js");
const bindings = JSON.parse(
  fs.readFileSync(
    path.join(
      __dirname,
      "../Assets/Live2DSource/FullActions/Model/action-bindings.json",
    ),
    "utf8",
  ),
);
function model(entries = bindings.parameters) {
  return {
    ids: entries.map((p) => p.id),
    minimumValues: entries.map((p) => p.min),
    maximumValues: entries.map((p) => p.max),
  };
}
const descriptor = { schemaVersion: 1, head: true, eyes: true };
const limits = G.capability(model(), bindings.parameters, descriptor);
function send(g, revision, now, enabled = true, x = 1, y = 0) {
  return g.receive(
    { type: "gaze", protocol: 1, generation: 1, revision, enabled, x, y },
    now,
  );
}
function run(g, from, count, idle = true, allowed = true, keepAlive = false) {
  for (let i = 0; i < count; i++) {
    const now = from + (i * 1000) / 60;
    if (keepAlive && i % 3 === 0)
      send(g, g.revision + 1, now, true, g.input.x, g.input.y);
    g.step(now, idle, allowed);
  }
}
test("six outfits must explicitly declare real head and eye capability before activation", () => {
  const root = path.join(__dirname, "../Assets/Live2DSource/FullActions/Model");
  for (const dir of [
    root,
    ...["office", "hoodie", "ox", "hero", "astronaut"].map((id) =>
      path.join(root, "outfits", id),
    ),
  ]) {
    const b = JSON.parse(
      fs.readFileSync(path.join(dir, "action-bindings.json"), "utf8"),
    );
    assert.deepEqual(
      G.capability(model(b.parameters), b.parameters, b.gaze)?.limits ?? null,
      b.gaze?.eyes ? G.limits : null,
    );
  }
});
test("absent, undeclared or non-directional axes disable gaze safely", () => {
  assert.equal(G.capability(model(), bindings.parameters), null);
  assert.equal(G.capability(model(), [], descriptor), null);
  assert.equal(
    G.capability(
      model(bindings.parameters.filter((p) => p.id !== "ParamEyeBallX")),
      bindings.parameters,
      descriptor,
    ),
    null,
  );
  const m = model();
  m.maximumValues[m.ids.indexOf("ParamEyeBallY")] = 0;
  assert.equal(G.capability(m, bindings.parameters, descriptor), null);
  assert.equal(
    G.capability(model(bindings.parameters.filter((p) => p.id !== "ParamAngleZ")), bindings.parameters, descriptor),
    null,
  );
});
test("actual Core limits further restrict authored safe limits", () => {
  const m = model();
  m.maximumValues[m.ids.indexOf("ParamAngleX")] = 3;
  const capability = G.capability(m, bindings.parameters, descriptor);
  assert.equal(capability.limits.ParamAngleX, 3);
  assert.deepEqual(capability.bounds.ParamAngleX, { min: -45, max: 3 });
});

test("only idle and stable office loops accept gaze", () => {
  for (const kind of Object.keys(A.durations))
    assert.equal(G.supports(kind), ["Idle", "WorkLoop", "BusyLoop"].includes(kind));
  assert.equal(G.supports("toString"), false);
});

test("office gaze adds to its authored head pose without changing typing or props", () => {
  for (const kind of ["WorkLoop", "BusyLoop"])
    for (const progress of [0, 0.17, 0.5, 0.8, 1])
      for (const [x, y] of [[1, 1], [-1, -1], [0, 0]]) {
        const g = new G.Tracking(1);
        send(g, 1, 0, true, x, y);
        run(g, 0, 180, true, true, true);
        const sample = A.sample(kind, progress), before = { ...sample.parameters };
        g.apply(sample.parameters, limits, kind);
        for (const [id, original] of Object.entries(before)) {
          const amplitude = G.profiles[kind][id];
          if (amplitude === undefined || amplitude === 0)
            assert.equal(sample.parameters[id], original, kind + " " + id);
        }
        assert.ok(Math.abs(sample.parameters.ParamAngleY - before.ParamAngleY - y * G.profiles[kind].ParamAngleY) < 0.001);
        assert.ok(Math.abs(sample.parameters.ParamAngleX - before.ParamAngleX - x * G.profiles[kind].ParamAngleX) < 0.001);
        assert.ok(Math.abs(sample.parameters.ParamAngleZ - before.ParamAngleZ - x * G.profiles[kind].ParamAngleZ) < 0.001);
        if (kind === "BusyLoop") {
          assert.equal(sample.parameters.ParamBusyEyes, 1);
          assert.equal(sample.parameters.ParamEyeBallX, undefined);
          assert.equal(sample.parameters.ParamEyeBallY, undefined);
        }
      }
});

test("zero gaze preserves work and busy head baselines exactly", () => {
  for (const kind of ["WorkLoop", "BusyLoop"]) {
    const q = A.sample(kind, 0.3).parameters, before = { ...q };
    new G.Tracking(1).apply(q, limits, kind);
    assert.equal(q.ParamAngleY, before.ParamAngleY);
    assert.equal(q.ParamAngleX, before.ParamAngleX);
  }
});

test("gaze clamps to actual asymmetric Core bounds rather than clipping the action to its offset budget", () => {
  const m = model();
  m.minimumValues[m.ids.indexOf("ParamAngleY")] = -18;
  m.maximumValues[m.ids.indexOf("ParamAngleX")] = 5;
  const ranges = G.capability(m, bindings.parameters, descriptor);
  const g = new G.Tracking(1);
  g.headX = 1; g.headY = -1;
  const q = A.sample("BusyLoop", 0.3).parameters;
  g.apply(q, ranges, "BusyLoop");
  assert.equal(q.ParamAngleY, -18);
  assert.equal(q.ParamAngleX, 5);
});
test("stale and malformed local samples cannot change tracking", () => {
  const g = new G.Tracking(1);
  send(g, 2, 0);
  assert.equal(send(g, 1, 1, true, -1), false);
  assert.equal(g.input.x, 1);
  assert.throws(() => send(g, 3, 2, true, NaN));
  assert.throws(() => send(g, 3, 2, true, 2));
  assert.throws(() =>
    g.receive(
      {
        type: "gaze",
        protocol: 1,
        generation: 2,
        revision: 3,
        enabled: true,
        x: 0,
        y: 0,
      },
      2,
    ),
  );
});
test("eyes lead the head without a first-frame snap", () => {
  const g = new G.Tracking(1);
  send(g, 1, 0);
  g.step(0, true, true);
  assert.equal(g.eyeX, 0);
  assert.equal(g.headX, 0);
  run(g, 1000 / 60, 6);
  assert.ok(g.eyeX > g.headX && g.headX > 0 && g.eyeX < 1);
});
test("four directions remain bounded and never alter body, feet, wings or mouth", () => {
  for (const [x, y] of [
    [1, 0],
    [-1, 0],
    [0, 1],
    [0, -1],
    [1, 1],
  ]) {
    const g = new G.Tracking(1);
    send(g, 1, 0, true, x, y);
    run(g, 0, 180, true, true, true);
    const q = A.neutral(),
      before = { ...q };
    g.apply(q, limits);
    for (const [id, value] of Object.entries(q)) {
      if (id in G.limits) assert.ok(Math.abs(value) <= G.limits[id]);
      else assert.equal(value, before[id], id);
    }
    assert.equal(Math.sign(q.ParamAngleX), Math.sign(x));
    assert.equal(Math.sign(q.ParamEyeBallY), Math.sign(y));
  }
});
test("distance exit or menu/drag/setting suppression returns smoothly to center", () => {
  const g = new G.Tracking(1);
  send(g, 1, 0);
  run(g, 0, 60, true, true, true);
  const before = g.headX;
  send(g, g.revision + 1, 1000, false);
  g.step(1000, true, false);
  assert.ok(g.headX > 0 && g.headX < before);
  run(g, 1017, 120, true, false);
  assert.ok(Math.abs(g.headX) < 0.001 && Math.abs(g.eyeX) < 0.001);
});
test("missing host samples expire rather than leaving a permanent stare", () => {
  const g = new G.Tracking(1);
  send(g, 1, 0);
  run(g, 0, 180);
  assert.ok(Math.abs(g.headX) < 0.001);
});
test("authored actions clear contribution and use the stage transition for handoff", () => {
  const g = new G.Tracking(1);
  send(g, 1, 0);
  run(g, 0, 30, true, true, true);
  g.step(500, false, false);
  assert.equal(g.headX, 0);
  assert.equal(g.eyeX, 0);
  g.step(600, true, true);
  assert.equal(g.headX, 0);
});
test("paused idle recenters without following new samples", () => {
  const g = new G.Tracking(1);
  send(g, 1, 0);
  run(g, 0, 30, true, true, true);
  send(g, g.revision + 1, 500, true, -1);
  run(g, 500, 90, true, false);
  assert.ok(g.headX >= 0 && g.headX < 0.002);
});
test("a long render stall is bounded and does not teleport the head", () => {
  const g = new G.Tracking(1);
  send(g, 1, 0);
  g.step(0, true, true);
  send(g, 2, 5000);
  g.step(5000, true, true);
  assert.ok(g.headX > 0 && g.headX < 0.21);
});
