"use strict";
(() => {
  function bindPoint(vertices, triangles, point) {
    for (let i = 0; i < triangles.length; i += 3) {
      const indices = Array.from(triangles.slice(i, i + 3));
      const [[ax, ay], [bx, by], [cx, cy]] = indices.map((index) => [
        vertices[index * 2],
        vertices[index * 2 + 1],
      ]);
      const determinant = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
      if (Math.abs(determinant) < 1e-8) continue;
      const u =
        ((by - cy) * (point[0] - cx) + (cx - bx) * (point[1] - cy)) /
        determinant;
      const v =
        ((cy - ay) * (point[0] - cx) + (ax - cx) * (point[1] - cy)) /
        determinant;
      const weights = [u, v, 1 - u - v];
      if (weights.every((weight) => weight >= -1e-6))
        return { indices, weights };
    }
    throw new Error("Authored attachment must be inside its mesh");
  }
  function point(vertices, binding) {
    return binding.indices.reduce(
      (result, index, i) => ({
        x: result.x + vertices[index * 2] * binding.weights[i],
        y: result.y + vertices[index * 2 + 1] * binding.weights[i],
      }),
      { x: 0, y: 0 },
    );
  }
  function corners(
    rect,
    rotation = 0,
    pivot = [rect[0] + rect[2] / 2, rect[1] + rect[3] / 2],
  ) {
    const [x, y, w, h] = rect,
      c = Math.cos(rotation),
      s = Math.sin(rotation);
    return [
      [x, y],
      [x, y + h],
      [x + w, y],
      [x + w, y + h],
    ].map(([px, py]) => [
      pivot[0] + (px - pivot[0]) * c - (py - pivot[1]) * s,
      pivot[1] + (px - pivot[0]) * s + (py - pivot[1]) * c,
    ]);
  }
  function heldRect(size, anchor, grip, scale = 1) {
    const width = size[0] * scale,
      height = size[1] * scale;
    return [
      anchor.x - width * grip[0],
      anchor.y - height * grip[1],
      width,
      height,
    ];
  }
  function aboveDesk(handY, surfaceY) {
    const value = Math.max(0, Math.min(1, (surfaceY - 12 - handY) / 10));
    return value * value * (3 - 2 * value);
  }
  function tear(eye, flow, side, amount = 1) {
    const wave = Math.sin((flow + (side === "l" ? 0.5 : 0)) * Math.PI * 2);
    return {
      rect: [
        eye.x - 6 + (side === "l" ? 2 : -2),
        eye.y,
        12,
        (24 + 1.5 * wave) * amount,
      ],
      opacity: 1,
    };
  }
  const api = { bindPoint, point, corners, heldRect, aboveDesk, tear };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else globalThis.EagleGeometry = api;
})();
