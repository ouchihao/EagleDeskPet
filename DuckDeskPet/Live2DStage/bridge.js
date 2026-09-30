"use strict";
(() => {
  function state(message, generation, previous) {
    if (
      !message ||
      message.protocol !== 1 ||
      message.generation !== generation ||
      message.type !== "state"
    )
      throw new Error("Invalid stage protocol or generation");
    for (const field of ["revision", "sequence"])
      if (!Number.isSafeInteger(message[field]) || message[field] < 0)
        throw new Error("Invalid " + field);
    if (previous && message.revision <= previous.revision) return null;
    if (
      !Object.hasOwn(globalThis.EagleActions.durations, message.kind) ||
      !["Idle", "Playing", "ReturningToIdle", "Terminal", "Paused"].includes(
        message.phase,
      ) ||
      typeof message.paused !== "boolean"
    )
      throw new Error("Invalid action state");
    if (
      !Number.isFinite(message.progress) ||
      message.progress < 0 ||
      message.progress > 1 ||
      !Number.isFinite(message.durationSeconds) ||
      message.durationSeconds < 0
    )
      throw new Error("Invalid timeline sample");
    if (
      message.kind !== "Idle" &&
      Math.abs(
        message.durationSeconds -
          globalThis.EagleActions.durations[message.kind],
      ) > 1e-6
    )
      throw new Error("Action duration differs from authored catalog");
    if (message.phase === "Terminal" && message.progress !== 1)
      throw new Error("Terminal must describe the real final pose");
    for (const slot of ["desk", "computer", "outfit"])
      if (typeof message.equipment?.[slot] !== "string")
        throw new Error("Missing equipment slot");
    return { ...message, equipment: { ...message.equipment } };
  }
  function progress(message, receivedAt, now) {
    if (message.paused || message.phase === "Terminal") return message.progress;
    const duration = message.kind === "Idle" ? 4 : message.durationSeconds;
    const elapsed = Math.max(0, now - receivedAt) / 1000;
    return message.kind === "Idle"
      ? (message.progress + elapsed / duration) % 1
      : Math.min(1, message.progress + elapsed / duration);
  }
  function align(next, previous, receivedAt, now) {
    if (previous?.kind === "Idle" && next.kind === "Idle")
      return { ...next, progress: progress(previous, receivedAt, now) };
    if (previous?.kind === next.kind && previous.sequence === next.sequence)
      return {
        ...next,
        progress: Math.max(next.progress, progress(previous, receivedAt, now)),
      };
    return next;
  }
  const equipmentKey = (e) => [e.desk, e.computer, e.outfit].join("|");
  function transitionProgress(clock, now, paused) {
    if (paused) clock.pausedAt ??= now;
    else if (clock.pausedAt !== undefined) {
      clock.startedAt += now - clock.pausedAt;
      clock.pausedAt = undefined;
    }
    return Math.max(
      0,
      Math.min(1, ((clock.pausedAt ?? now) - clock.startedAt) / 120),
    );
  }
  const api = { state, progress, align, equipmentKey, transitionProgress };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else globalThis.EagleBridge = api;
})();
