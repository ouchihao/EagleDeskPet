"use strict";
(() => {
  const limits = Object.freeze({
    ParamAngleX: 9,
    ParamAngleY: 5,
    ParamEyeBallX: 0.35,
    ParamEyeBallY: 0.3,
  });
  function capability(parameters, declared, descriptor) {
    if (
      descriptor?.schemaVersion !== 1 ||
      descriptor.head !== true ||
      descriptor.eyes !== true
    )
      return null;
    const metadata = new Map((declared || []).map((p) => [p.id, p]));
    const result = {};
    for (const [id, maximum] of Object.entries(limits)) {
      const index = parameters.ids.indexOf(id),
        entry = metadata.get(id);
      if (index < 0 || !entry || entry.default !== 0) return null;
      const min = parameters.minimumValues[index],
        max = parameters.maximumValues[index];
      if (
        ![min, max, entry.min, entry.max].every(Number.isFinite) ||
        min >= 0 ||
        max <= 0 ||
        entry.min >= 0 ||
        entry.max <= 0
      )
        return null;
      result[id] = Math.min(maximum, -min, max, -entry.min, entry.max);
    }
    return Object.freeze(result);
  }
  class Tracking {
    constructor(generation) {
      this.generation = generation;
      this.revision = -1;
      this.receivedAt = -Infinity;
      this.input = { enabled: false, x: 0, y: 0 };
      this.reset();
    }
    reset() {
      this.eyeX = this.eyeY = this.headX = this.headY = 0;
      this.lastTime = undefined;
    }
    receive(message, now) {
      if (
        message?.protocol !== 1 ||
        message.type !== "gaze" ||
        message.generation !== this.generation ||
        !Number.isSafeInteger(message.revision) ||
        message.revision < 0 ||
        typeof message.enabled !== "boolean" ||
        ![message.x, message.y].every(
          (x) => Number.isFinite(x) && Math.abs(x) <= 1,
        )
      )
        throw new Error("Invalid local gaze sample");
      if (message.revision <= this.revision) return false;
      this.revision = message.revision;
      this.input = { enabled: message.enabled, x: message.x, y: message.y };
      this.receivedAt = now;
      return true;
    }
    step(now, idle, allowed) {
      if (!idle) {
        this.reset();
        return;
      }
      const seconds =
        this.lastTime === undefined
          ? 0
          : Math.max(0, Math.min(0.05, (now - this.lastTime) / 1000));
      this.lastTime = now;
      const following =
        allowed && this.input.enabled && now - this.receivedAt <= 350;
      const x = following ? this.input.x : 0,
        y = following ? this.input.y : 0;
      const eyeBlend = 1 - Math.exp(-seconds / (following ? 0.075 : 0.16));
      const headBlend = 1 - Math.exp(-seconds / 0.22);
      this.eyeX += (x - this.eyeX) * eyeBlend;
      this.eyeY += (y - this.eyeY) * eyeBlend;
      this.headX += (x - this.headX) * headBlend;
      this.headY += (y - this.headY) * headBlend;
    }
    apply(parameters, ranges) {
      if (!ranges) return;
      const values = {
        ParamAngleX: this.headX,
        ParamAngleY: this.headY,
        ParamEyeBallX: this.eyeX,
        ParamEyeBallY: this.eyeY,
      };
      for (const [id, value] of Object.entries(values)) {
        parameters[id] = Math.max(
          -ranges[id],
          Math.min(ranges[id], (parameters[id] || 0) + value * ranges[id]),
        );
      }
    }
    report() {
      return {
        revision: this.revision,
        input: { ...this.input },
        eyeX: this.eyeX,
        eyeY: this.eyeY,
        headX: this.headX,
        headY: this.headY,
      };
    }
  }
  const api = { capability, Tracking, limits };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else globalThis.EagleGaze = api;
})();
