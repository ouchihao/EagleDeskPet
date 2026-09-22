"use strict";
(() => {
  const canvas = document.getElementById("stage");
  const gl = canvas.getContext("webgl", { alpha: true, premultipliedAlpha: true, antialias: false, preserveDrawingBuffer: true });
  if (!gl) throw new Error("WebGL is unavailable");
  function shader(type, source) {
    const result = gl.createShader(type);
    gl.shaderSource(result, source); gl.compileShader(result);
    if (!gl.getShaderParameter(result, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(result));
    return result;
  }
  const vertex = shader(gl.VERTEX_SHADER, "attribute vec2 p; void main(){gl_Position=vec4(p,0.,1.);}");
  const fragment = shader(gl.FRAGMENT_SHADER, "precision mediump float; uniform vec4 c; void main(){gl_FragColor=c;}");
  const program = gl.createProgram();
  gl.attachShader(program, vertex); gl.attachShader(program, fragment); gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
  gl.useProgram(program);
  const buffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  const location = gl.getAttribLocation(program, "p");
  gl.enableVertexAttribArray(location); gl.vertexAttribPointer(location, 2, gl.FLOAT, false, 0, 0);
  const color = gl.getUniformLocation(program, "c");
  function rect(x, y, w, h, rgba) {
    const left = x / 192 - 1, right = (x + w) / 192 - 1;
    const top = 1 - y / 173, bottom = 1 - (y + h) / 173;
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([left, top, right, top, left, bottom, left, bottom, right, top, right, bottom]), gl.DYNAMIC_DRAW);
    gl.uniform4fv(color, rgba); gl.drawArrays(gl.TRIANGLES, 0, 6);
  }
  const colors = { body: [0.8, 0.25, 0.1, 1], desk: [0.1, 0.3, 0.9, 1], hand: [0.1, 0.8, 0.3, 1], apron: [0.6, 0.2, 0.8, 1], computer: [0.9, 0.8, 0.15, 1] };
  let previous = null, frames = [], elapsed = 0;
  function draw(now) {
    if (previous !== null) { frames.push(now - previous); if (frames.length > 36000) frames.shift(); }
    previous = now; elapsed = now;
    gl.viewport(0, 0, 384, 346); gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    rect(142, 60, 100, 225, colors.body);
    rect(32, 190, 320, 90, colors.desk);
    rect(167, 180, 50, 35, colors.hand);
    rect(32, 245, 320, 35, colors.apron);
    rect(248, 145, 70, 90, colors.computer);
    rect(55 + 12 * Math.sin(now / 200), 85, 20, 20, [0.7, 0.4, 0.9, 1]);
    requestAnimationFrame(draw);
  }
  function pixel(x, y) {
    const result = new Uint8Array(4); gl.readPixels(x, 345 - y, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, result); return [...result];
  }
  function matches(actual, expected) { return actual.every((v, i) => Math.abs(v - Math.round(expected[i] * 255)) <= 1); }
  window.probe = Object.freeze({
    resetMetrics() { previous = null; frames = []; },
    metrics() {
      const ordered = [...frames].sort((a, b) => a - b), total = frames.reduce((a, b) => a + b, 0);
      return { callbackCount: frames.length, callbackHz: total ? 1000 * frames.length / total : 0,
        p95CallbackIntervalMs: ordered[Math.floor(ordered.length * .95)] ?? null,
        over100ms: frames.filter(x => x > 100).length,
        meaning: "requestAnimationFrame scheduling only; not physical presentation FPS" };
    }
  });
  requestAnimationFrame(time => {
    draw(time);
    const tests = { clear: matches(pixel(370, 330), [0, 0, 0, 0]),
      bodyBehindDesk: matches(pixel(150, 220), colors.desk),
      handAboveDesk: matches(pixel(192, 200), colors.hand),
      apronAboveBody: matches(pixel(192, 260), colors.apron),
      computerAboveDesk: matches(pixel(280, 210), colors.computer) };
    window.chrome.webview.postMessage({ protocol: 1, type: "stage-probe-ready", passed: Object.values(tests).every(Boolean), tests });
  });
})();
