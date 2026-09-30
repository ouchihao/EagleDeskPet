"use strict";
(() => {
  const durations = Object.freeze({
    Idle: 4,
    Yawn: 2,
    Shy: 2,
    Eat: 2,
    WorkEnter: 3.5,
    WorkLoop: 2,
    WorkToBusy: 1.5,
    BusyLoop: 2,
    WorkExit: 2,
    BusyExit: 2,
    HungryEnter: 1.5,
    HungryLoop: 2,
    HungryExit: 1.5,
    Tea: 2,
    Annoyed: 2,
  });
  const unit = (x) => Math.max(0, Math.min(1, x));
  const smooth = (x) => {
    x = unit(x);
    return x * x * (3 - 2 * x);
  };
  const ramp = (p, a, b) => smooth((p - a) / (b - a));
  const envelope = (p, a = 0.12, b = 0.32, c = 0.72, d = 0.96) =>
    ramp(p, a, b) * (1 - ramp(p, c, d));
  function track(p, keys) {
    if (p <= keys[0][0]) return keys[0][1];
    for (let i = 1; i < keys.length; i++)
      if (p <= keys[i][0]) {
        const [a, x] = keys[i - 1],
          [b, y] = keys[i];
        return x + (y - x) * smooth((p - a) / (b - a));
      }
    return keys[keys.length - 1][1];
  }
  const neutral = () => ({
    ParamAngleX: 0,
    ParamAngleY: 0,
    ParamAngleZ: 0,
    ParamBodyAngleX: 0,
    ParamBodyAngleY: 0,
    ParamBodyAngleZ: 0,
    ParamBreath: 0,
    ParamEyeLOpen: 1,
    ParamEyeROpen: 1,
    ParamBrowLY: 0,
    ParamBrowRY: 0,
    ParamWingLLift: 0,
    ParamWingRLift: 0,
    ParamWingLSwing: 0,
    ParamWingRSwing: 0,
    ParamWingLFold: 0,
    ParamWingRFold: 0,
    ParamBodySquash: 0,
    ParamBeakOpen: 0,
    ParamBusyEyes: 0,
  });
  function mix(target, pose, amount = 1) {
    for (const [id, v] of Object.entries(pose))
      target[id] += (v - target[id]) * amount;
  }
  const workPose = {
    ParamAngleY: -7,
    ParamEyeLOpen: 0.85,
    ParamEyeROpen: 0.85,
    ParamWingLLift: 0.4,
    ParamWingRLift: 0.4,
    ParamWingLFold: 0.1,
    ParamWingRFold: 0.1,
    ParamBodySquash: 0.08,
  };
  const busyPose = {
    ...workPose,
    ParamAngleY: -14,
    ParamEyeLOpen: 1,
    ParamEyeROpen: 1,
    ParamBrowLY: -0.8,
    ParamBrowRY: -0.8,
    ParamBodySquash: 0.15,
    ParamBusyEyes: 1,
  };
  const hungryPose = {
    ParamAngleY: -8,
    ParamEyeLOpen: 0.78,
    ParamEyeROpen: 0.78,
    ParamBrowLY: 0.8,
    ParamBrowRY: 0.8,
    ParamWingLLift: 0.3,
    ParamWingRLift: 0.3,
    ParamWingLFold: 0.38,
    ParamWingRFold: 0.38,
    ParamBodySquash: 0.12,
    ParamBeakOpen: 0.04,
  };
  function typing(q, p, busy = false, weight = 1) {
    const wave = Math.sin(p * Math.PI * (busy ? 16 : 8)) * weight;
    q.ParamWingLLift += wave * 0.055;
    q.ParamWingRLift -= wave * 0.055;
    q.ParamWingLFold += wave * 0.025;
    q.ParamWingRFold -= wave * 0.025;
    q.ParamAngleZ += Math.sin(p * Math.PI * 2) * (busy ? 4 : 2) * weight;
  }
  function sample(kind, progress) {
    if (!Object.hasOwn(durations, kind) || !Number.isFinite(progress))
      throw new Error("Unknown action or invalid progress");
    const p = unit(progress),
      q = neutral(),
      props = {
        bowl: 0,
        rice: 0,
        tea: 0,
        teaTilt: 0,
        tears: 0,
        tearFlow: 0,
        anger: 0,
      };
    const e = envelope(p),
      sway = Math.sin(p * Math.PI * 4) * e;
    switch (kind) {
      case "Idle": {
        const breath = (1 - Math.cos(p * Math.PI * 2)) * 0.25;
        q.ParamBreath = breath;
        q.ParamAngleZ = Math.sin(p * Math.PI * 2) * 1.8;
        const blink = envelope(p, 0.59, 0.625, 0.645, 0.68);
        q.ParamEyeLOpen = q.ParamEyeROpen = 1 - blink;
        break;
      }
      case "Yawn":
        q.ParamAngleY = track(p, [
          [0, 0],
          [0.22, 8],
          [0.48, 17],
          [0.67, 10],
          [1, 0],
        ]);
        q.ParamBeakOpen = track(p, [
          [0, 0],
          [0.18, 0.05],
          [0.36, 1],
          [0.57, 1],
          [0.76, 0.2],
          [0.94, 0],
          [1, 0],
        ]);
        q.ParamWingRLift = track(p, [
          [0, 0],
          [0.14, 0.08],
          [0.35, 0.88],
          [0.64, 0.88],
          [0.85, 0.28],
          [1, 0],
        ]);
        q.ParamWingRFold = 0.4 * e;
        q.ParamEyeLOpen = q.ParamEyeROpen =
          1 - 0.98 * envelope(p, 0.08, 0.27, 0.68, 0.9);
        q.ParamBodySquash = -0.38 * e;
        q.ParamBreath = 0.5 * e;
        break;
      case "Shy":
        q.ParamAngleZ = sway * 18;
        q.ParamAngleX = -sway * 8;
        q.ParamBodyAngleZ = -sway * 4;
        q.ParamBodyAngleX = sway * 3;
        q.ParamBodySquash = (0.15 + 0.1 * Math.sin(p * Math.PI * 8)) * e;
        q.ParamWingRLift = 0.84 * e;
        q.ParamWingRFold = 0.43 * e;
        q.ParamWingLLift = 0.14 * e;
        q.ParamWingLFold = 0.12 * e;
        q.ParamWingRSwing = 0.06 * sway;
        q.ParamEyeLOpen = q.ParamEyeROpen = 1 - 0.6 * e;
        q.ParamBrowLY = q.ParamBrowRY = 0.35 * e;
        break;
      case "Eat": {
        props.bowl = envelope(p, 0.03, 0.18, 0.8, 0.98);
        props.rice = props.bowl;
        const take = track(p, [
          [0, 0],
          [0.18, 0],
          [0.3, 1],
          [0.4, 1],
          [0.5, 0],
          [0.62, 0],
          [0.72, 1],
          [0.79, 1],
          [0.88, 0],
          [1, 0],
        ]);
        q.ParamWingLLift = q.ParamWingRLift = 0.34 * e;
        q.ParamWingLFold = q.ParamWingRFold = (0.34 + 0.39 * take) * e;
        q.ParamBeakOpen =
          (0.14 + 0.35 * take + 0.1 * Math.sin(p * Math.PI * 16) ** 2) * e;
        q.ParamAngleY = (-3 - 3 * take) * e;
        q.ParamBodySquash = 0.1 * e;
        q.ParamEyeLOpen = q.ParamEyeROpen = 1 - (0.3 + 0.35 * take) * e;
        break;
      }
      case "WorkEnter":
        mix(q, workPose, ramp(p, 0.49, 0.91));
        q.ParamAngleX = track(p, [
          [0, 0],
          [0.2, -15],
          [0.38, 8],
          [0.55, 0],
          [1, 0],
        ]);
        q.ParamAngleY += 8 * envelope(p, 0.18, 0.29, 0.43, 0.55);
        q.ParamEyeLOpen -= 0.7 * envelope(p, 0.35, 0.4, 0.44, 0.5);
        q.ParamEyeROpen = q.ParamEyeLOpen;
        break;
      case "WorkLoop":
        mix(q, workPose);
        typing(q, p);
        break;
      case "WorkToBusy":
        mix(q, workPose);
        mix(q, busyPose, ramp(p, 0.08, 0.88));
        typing(q, p, true, Math.sin(Math.PI * p) ** 2);
        q.ParamBusyEyes = p < 0.46 ? 0 : 1;
        q.ParamEyeLOpen = q.ParamEyeROpen = track(p, [
          [0, 0.85],
          [0.3, 0.85],
          [0.42, 0],
          [0.5, 0],
          [0.68, 1],
          [1, 1],
        ]);
        props.anger = ramp(p, 0.25, 0.85);
        break;
      case "BusyLoop":
        mix(q, busyPose);
        typing(q, p, true);
        q.ParamBodySquash += 0.06 * Math.sin(p * Math.PI * 8);
        props.anger = 1;
        break;
      case "WorkExit":
        mix(q, workPose, 1 - ramp(p, 0.05, 0.54));
        break;
      case "BusyExit":
        mix(q, busyPose, 1 - ramp(p, 0.02, 0.59));
        q.ParamBusyEyes = p < 0.32 ? 1 : 0;
        q.ParamEyeLOpen = q.ParamEyeROpen = track(p, [
          [0, 1],
          [0.16, 1],
          [0.28, 0],
          [0.36, 0],
          [0.55, 1],
          [1, 1],
        ]);
        props.anger = 1 - ramp(p, 0, 0.45);
        break;
      case "HungryEnter":
        mix(q, hungryPose, ramp(p, 0.12, 0.7));
        props.bowl = ramp(p, 0.44, 0.86);
        props.tears = ramp(p, 0.7, 1);
        break;
      case "HungryLoop": {
        mix(q, hungryPose);
        const sniff =
          envelope(p, 0.24, 0.3, 0.34, 0.4) +
          0.65 * envelope(p, 0.46, 0.51, 0.54, 0.61);
        q.ParamAngleZ = Math.sin(p * Math.PI * 2) * 2;
        q.ParamAngleY -= 1.5 * sniff;
        q.ParamBodySquash += 0.035 * sniff;
        q.ParamEyeLOpen -= 0.13 * sniff;
        q.ParamEyeROpen -= 0.13 * sniff;
        q.ParamBeakOpen += 0.06 * sniff;
        q.ParamWingLLift += 0.008 * sniff;
        q.ParamWingRLift += 0.008 * sniff;
        props.bowl = 1;
        props.tears = 1;
        props.tearFlow = p;
        break;
      }
      case "HungryExit":
        mix(q, hungryPose, 1 - ramp(p, 0.05, 0.62));
        props.bowl = 1 - ramp(p, 0.05, 0.43);
        props.tears = 1 - ramp(p, 0, 0.3);
        break;
      case "Tea":
        props.tea = envelope(p, 0.03, 0.2, 0.81, 0.98);
        props.teaTilt = -0.24 * envelope(p, 0.37, 0.47, 0.64, 0.76);
        q.ParamWingRLift = track(p, [
          [0, 0],
          [0.2, 0.26],
          [0.42, 0.69],
          [0.65, 0.69],
          [0.85, 0.3],
          [1, 0],
        ]);
        q.ParamWingRFold = 0.34 * e;
        q.ParamAngleY = 2 * e;
        q.ParamAngleZ = -3 * e;
        q.ParamEyeLOpen = q.ParamEyeROpen = 1 - 0.75 * e;
        q.ParamBeakOpen = 0.22 * envelope(p, 0.35, 0.43, 0.62, 0.7);
        break;
      case "Annoyed":
        q.ParamWingRLift = (0.4 + 0.06 * Math.sin(p * Math.PI * 8)) * e;
        q.ParamWingRFold = 0.26 * e;
        q.ParamWingLLift = 0.08 * e;
        q.ParamAngleX = sway * 18;
        q.ParamAngleY = -7 * e;
        q.ParamBodySquash = 0.16 * e;
        q.ParamBrowLY = q.ParamBrowRY = -0.85 * e;
        q.ParamEyeLOpen = q.ParamEyeROpen = 1 - 0.53 * e;
        q.ParamBeakOpen = (0.4 + 0.2 * Math.sin(p * Math.PI * 10)) * e;
        props.anger = e;
        break;
    }
    return {
      kind,
      progress: p,
      durationSeconds: durations[kind],
      parameters: q,
      props,
    };
  }
  function scene(kind, p, definition) {
    const seconds = unit(p) * durations[kind],
      m = definition.motion,
      f = definition.fire;
    const interval = (x) =>
      unit((seconds - x.startSeconds) / x.durationSeconds);
    const result = {
      visible: false,
      computer: false,
      deskX: 0,
      computerY: 0,
      fire: 0,
    };
    if (kind.startsWith("Hungry")) {
      result.visible = true;
      const t =
        kind === "HungryEnter"
          ? unit(seconds / 0.65)
          : kind === "HungryExit"
            ? 1 - unit((seconds - 0.75) / 0.75)
            : 1;
      result.deskX = -m.deskTravelPixels * (1 - smooth(t));
      return result;
    }
    if (
      ![
        "WorkEnter",
        "WorkLoop",
        "WorkToBusy",
        "BusyLoop",
        "WorkExit",
        "BusyExit",
      ].includes(kind)
    )
      return result;
    result.visible = result.computer = true;
    if (kind === "WorkEnter") {
      const t = interval(m.computerEnter),
        land = m.dropLandingFraction,
        end = m.dropFirstBounceEndFraction;
      result.deskX = -m.deskTravelPixels * (1 - interval(m.deskEnter)) ** 3;
      result.computerY =
        t < land
          ? -m.computerTravelPixels * (1 - (t / land) ** 2)
          : t < end
            ? -m.dropFirstBouncePixels *
              Math.sin((Math.PI * (t - land)) / (end - land))
            : -m.dropSecondBouncePixels *
              Math.sin((Math.PI * (t - end)) / (1 - end));
    } else if (kind.endsWith("Exit")) {
      result.deskX = -m.deskTravelPixels * interval(m.deskExit) ** 3;
      result.computerY =
        -m.computerTravelPixels * interval(m.computerExit) ** 3;
    }
    result.fire =
      kind === "BusyLoop"
        ? 1
        : kind === "WorkToBusy"
          ? smooth(interval(f.grow))
          : kind === "BusyExit"
            ? 1 - smooth(interval(f.shrink))
            : 0;
    return result;
  }
  const api = { durations, neutral, sample, scene, track, smooth };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else globalThis.EagleActions = api;
})();
