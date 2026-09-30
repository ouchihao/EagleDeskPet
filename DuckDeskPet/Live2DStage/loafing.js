"use strict";
(() => {
  const duration = 2.8;
  const unit = (x) => Math.max(0, Math.min(1, x));
  const smooth = (x) => { x = unit(x); return x * x * (3 - 2 * x); };
  const ramp = (s, a, b) => smooth((s - a) / (b - a));
  const mix = (a, b, t) => a + (b - a) * t;
  const phases = new Set(["none", "throw", "loop", "return"]);
  const kinds = new Set(["None", "NosePicking", "WoodenFish"]);

  class Tracking {
    constructor(generation) {
      this.generation = generation;
      this.revision = -1;
      this.phase = "none";
      this.kind = "None";
      this.seconds = 0;
      this.paused = true;
      this.receivedAt = 0;
      this.pose = {};
    }
    elapsed(now) {
      const elapsed = this.seconds + (this.paused ? 0 : Math.max(0, now - this.receivedAt) / 1000);
      return this.phase === "loop" ? elapsed : Math.min(duration, elapsed);
    }
    receive(message, now, parameters = {}) {
      if (message?.protocol !== 1 || message.type !== "loafing" ||
          message.generation !== this.generation ||
          !Number.isSafeInteger(message.revision) || message.revision < 0 ||
          !phases.has(message.phase) || !kinds.has(message.kind) ||
          (message.phase !== "none" && message.kind === "None") ||
          !Number.isFinite(message.seconds) || message.seconds < 0 ||
          typeof message.paused !== "boolean")
        throw new Error("Invalid local loafing sample");
      if (message.revision <= this.revision) return false;
      const changed = message.phase !== this.phase || message.kind !== this.kind;
      const previous = this.elapsed(now);
      if (changed) this.pose = { ...parameters };
      this.seconds = changed ? message.seconds : Math.max(message.seconds, previous);
      this.phase = message.phase;
      this.kind = message.kind;
      this.paused = message.paused;
      this.receivedAt = now;
      this.revision = message.revision;
      return true;
    }
    sample(now) {
      return { ...sample(this.phase, this.elapsed(now), this.pose),
        kind: this.kind, paused: this.paused, revision: this.revision };
    }
  }

  function sample(phase, seconds, source = {}) {
    const s = phase === "loop" ? seconds : Math.max(0, Math.min(duration, seconds));
    const result = {
      active: phase !== "none", phase, seconds: s,
      deskX: 0, modelVisible: phase !== "loop", modelOffsetY: 0,
      computerVisible: phase !== "loop", computerX: 0, computerY: 0,
      computerRotation: 0, fire: 0, parameters: { ...source },
    };
    if (phase === "none") return result;
    const p = result.parameters;
    const relax = (t) => {
      for (const id of ["ParamAngleX", "ParamAngleZ", "ParamBodyAngleX", "ParamBodyAngleY", "ParamBodyAngleZ", "ParamBreath"])
        p[id] = mix(source[id] || 0, 0, t);
      p.ParamAngleY = mix(source.ParamAngleY || 0, 4, t);
      p.ParamBrowLY = mix(source.ParamBrowLY || 0, 0.12, t);
      p.ParamBrowRY = mix(source.ParamBrowRY || 0, 0.12, t);
      p.ParamBodySquash = mix(source.ParamBodySquash || 0, 0.24, t);
    };
    if (phase === "throw") {
      relax(ramp(s, 0, 0.45));
      const close = 1 - ramp(s, 0, 0.15), open = ramp(s, 0.15, 0.3);
      p.ParamEyeLOpen = s < 0.15 ? (source.ParamEyeLOpen ?? 1) * close : open;
      p.ParamEyeROpen = s < 0.15 ? (source.ParamEyeROpen ?? 1) * close : open;
      p.ParamBusyEyes = s < 0.15 ? (source.ParamBusyEyes || 0) : 0;
      const lift = ramp(s, 0.08, 0.34) * (1 - ramp(s, 0.55, 1.05));
      p.ParamWingRLift = mix(source.ParamWingRLift || 0, 0.86, lift);
      p.ParamWingRFold = mix(source.ParamWingRFold || 0, 0.36, lift);
      p.ParamWingRSwing = mix(source.ParamWingRSwing || 0, 0.36, lift);
      result.modelOffsetY = 300 * ramp(s, 1.15, 1.95);
      result.modelVisible = s < 1.95;
      const flight = unit((s - 0.25) / 0.9);
      result.computerVisible = s < 1.15;
      result.computerX = 440 * flight * flight;
      result.computerY = -340 * flight + 40 * flight * flight;
      result.computerRotation = -Math.PI * 1.5 * flight;
      result.fire = (source.ParamBusyEyes || 0) * (1 - ramp(s, 0, 0.28));
    } else if (phase === "loop") {
      result.modelOffsetY = 300;
    } else if (phase === "return") {
      const rising = ramp(s, 0.8, 1.6);
      relax(1 - rising);
      result.modelOffsetY = 300 * (1 - rising);
      result.modelVisible = s > 0.8;
      const landing = ramp(s, 1.4, 2.8);
      result.computerVisible = s > 1.4;
      result.computerX = 440 * (1 - landing);
      result.computerY = -300 * (1 - landing);
      result.computerRotation = -Math.PI * 1.5 * (1 - landing);
      const catching = ramp(s, 1.4, 2.1) * (1 - ramp(s, 2.1, 2.8));
      p.ParamWingRLift = mix(source.ParamWingRLift || 0, 0.7, catching);
      p.ParamWingRFold = mix(source.ParamWingRFold || 0, 0.3, catching);
      const close = 1 - ramp(s, 2.28, 2.48), open = ramp(s, 2.48, 2.7);
      p.ParamEyeLOpen = s < 2.48 ? close : (source.ParamEyeLOpen ?? 1) * open;
      p.ParamEyeROpen = s < 2.48 ? close : (source.ParamEyeROpen ?? 1) * open;
      p.ParamBusyEyes = s < 2.48 ? 0 : (source.ParamBusyEyes || 0);
      result.fire = (source.ParamBusyEyes || 0) * ramp(s, 2.55, 2.8);
    }
    return result;
  }
  const api = { duration, Tracking, sample };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else globalThis.EagleLoafing = api;
})();
