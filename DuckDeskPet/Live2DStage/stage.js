"use strict";
(() => {
  const A = globalThis.EagleActions,
    B = globalThis.EagleBridge,
    G = globalThis.EagleGeometry,
    canvas = document.getElementById("stage");
  const query = new URLSearchParams(location.search),
    qa = query.get("qa") === "1",
    generation = Number(query.get("generation") || (qa ? 1 : NaN));
  const metrics = {
    frames: 0,
    coreUpdates: 0,
    drawPasses: 0,
    modelsLoaded: 0,
    modelsReleased: 0,
    modelVisiblePixels: {},
    errors: [],
    glErrors: [],
    nonFiniteVertices: 0,
    maximumFootDelta: 0,
    requiredParameters: Object.keys(A.neutral()),
    missingParameters: [],
    terminal: [],
    intervals: [],
  };
  const textures = new Map(),
    models = new Map(),
    prepared = new Map();
  let config,
    catalog,
    scene,
    gl,
    framework,
    quad,
    foregroundTarget,
    active,
    current,
    receivedAt = 0,
    lastTime,
    raf,
    dead = false,
    prepareRevision = 0,
    presentedKey = "",
    presentedCandidate = null,
    terminalKey = "",
    terminalCandidate = null,
    frameSerial = 0,
    qaRevision = 0,
    recording,
    lastParameters,
    transitionFrom,
    transitionClock,
    frozenParameters,
    neutralizing,
    neutralizeRequest,
    bridgeRevision = -1,
    rasterFrames = new Map(),
    rasterFrame,
    rasterPreparation = 0;
  const post = (message) => {
    if (globalThis.chrome?.webview)
      globalThis.chrome.webview.postMessage({
        protocol: 1,
        generation,
        ...message,
      });
  };
  const require = (value, message) => {
    if (!value) throw new Error(message);
  };
  function fail(error) {
    if (dead) return;
    dead = true;
    const message = String(error?.stack || error).slice(0, 2000);
    metrics.errors.push(message);
    document.body.classList.add("failed");
    document.getElementById("status").textContent = message;
    post({ type: "error", message });
  }
  window.addEventListener("error", (event) =>
    fail(event.error || event.message),
  );
  window.addEventListener("unhandledrejection", (event) => fail(event.reason));
  function local(path, base = location.href) {
    const url = new URL(path, base);
    require(url.origin === location.origin &&
      !url.search &&
      !url.hash, "Only local immutable asset URLs are allowed");
    return url.href;
  }
  async function json(path) {
    const response = await fetch(local(path), { cache: "no-cache" });
    require(response.ok, "Missing local JSON " + path);
    return response.json();
  }
  function asset(path) {
    return local(
      path.startsWith("Assets/") ? path.slice(7) : path,
      new URL(config.assetRoot, location.href),
    );
  }
  async function texture(url) {
    url = local(url);
    if (textures.has(url)) return textures.get(url);
    const pending = new Promise((resolve, reject) => {
      const image = new Image();
      image.onload = () => {
        try {
          require(image.width <= gl.getParameter(gl.MAX_TEXTURE_SIZE) &&
            image.height <=
              gl.getParameter(gl.MAX_TEXTURE_SIZE), "Texture too large");
          const id = gl.createTexture();
          gl.bindTexture(gl.TEXTURE_2D, id);
          gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
          gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
          gl.texImage2D(
            gl.TEXTURE_2D,
            0,
            gl.RGBA,
            gl.RGBA,
            gl.UNSIGNED_BYTE,
            image,
          );
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
          resolve({ id, width: image.width, height: image.height });
        } catch (error) {
          reject(error);
        }
      };
      image.onerror = () => reject(new Error("Texture load failed " + url));
      image.src = url;
    });
    textures.set(url, pending);
    return pending;
  }
  class Quads {
    constructor() {
      const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        require(gl.getShaderParameter(
          shader,
          gl.COMPILE_STATUS,
        ), gl.getShaderInfoLog(shader));
        return shader;
      };
      const vs = compile(
          gl.VERTEX_SHADER,
          "attribute vec2 a;attribute vec2 uv;varying vec2 v;void main(){v=uv;gl_Position=vec4(a,0.,1.);}",
        ),
        fs = compile(
          gl.FRAGMENT_SHADER,
          "precision mediump float;uniform sampler2D image;uniform float alpha;varying vec2 v;void main(){gl_FragColor=texture2D(image,v)*alpha;}",
        );
      this.program = gl.createProgram();
      gl.attachShader(this.program, vs);
      gl.attachShader(this.program, fs);
      gl.linkProgram(this.program);
      require(gl.getProgramParameter(
        this.program,
        gl.LINK_STATUS,
      ), gl.getProgramInfoLog(this.program));
      gl.deleteShader(vs);
      gl.deleteShader(fs);
      this.buffer = gl.createBuffer();
      this.position = gl.getAttribLocation(this.program, "a");
      this.uv = gl.getAttribLocation(this.program, "uv");
      this.alpha = gl.getUniformLocation(this.program, "alpha");
      this.sampler = gl.getUniformLocation(this.program, "image");
    }
    draw(image, rect = [0, 0, 384, 346], alpha = 1, rotation = 0, pivot) {
      if (!image || alpha <= 0) return;
      const points = G.corners(rect, rotation, pivot).map(([x, y]) => [
        x / 192 - 1,
        1 - y / 173,
      ]);
      const uv = [
        [0, 0],
        [0, 1],
        [1, 0],
        [1, 1],
      ];
      gl.useProgram(this.program);
      gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
      gl.bufferData(
        gl.ARRAY_BUFFER,
        new Float32Array(
          [0, 1, 2, 2, 1, 3].flatMap((index) => [
            ...points[index],
            ...uv[index],
          ]),
        ),
        gl.STREAM_DRAW,
      );
      gl.enableVertexAttribArray(this.position);
      gl.enableVertexAttribArray(this.uv);
      gl.vertexAttribPointer(this.position, 2, gl.FLOAT, false, 16, 0);
      gl.vertexAttribPointer(this.uv, 2, gl.FLOAT, false, 16, 8);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, image.id);
      gl.uniform1i(this.sampler, 0);
      gl.uniform1f(this.alpha, alpha);
      gl.disable(gl.DEPTH_TEST);
      gl.disable(gl.CULL_FACE);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
    }
    release() {
      gl.deleteBuffer(this.buffer);
      gl.deleteProgram(this.program);
    }
  }
  class ForegroundTarget {
    constructor() {
      this.image = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, this.image);
      gl.texImage2D(
        gl.TEXTURE_2D,
        0,
        gl.RGBA,
        canvas.width,
        canvas.height,
        0,
        gl.RGBA,
        gl.UNSIGNED_BYTE,
        null,
      );
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      this.framebuffer = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.framebuffer);
      gl.framebufferTexture2D(
        gl.FRAMEBUFFER,
        gl.COLOR_ATTACHMENT0,
        gl.TEXTURE_2D,
        this.image,
        0,
      );
      require(gl.checkFramebufferStatus(gl.FRAMEBUFFER) ===
        gl.FRAMEBUFFER_COMPLETE, "Foreground target unavailable");
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        require(gl.getShaderParameter(
          shader,
          gl.COMPILE_STATUS,
        ), gl.getShaderInfoLog(shader));
        return shader;
      };
      const vs = compile(
          gl.VERTEX_SHADER,
          "attribute vec2 a;varying vec2 v;void main(){v=(a+1.)*.5;gl_Position=vec4(a,0.,1.);}",
        ),
        fs = compile(
          gl.FRAGMENT_SHADER,
          "precision mediump float;uniform sampler2D hands;uniform sampler2D desk;uniform vec4 maskRect;uniform vec2 pivot;uniform float rotation;uniform float visibility;uniform float edge;varying vec2 v;void main(){vec2 p=vec2(v.x*384.,(1.-v.y)*346.)-pivot;float c=cos(rotation),s=sin(rotation);p=vec2(p.x*c+p.y*s,-p.x*s+p.y*c)+pivot;vec2 uv=(p-maskRect.xy)/maskRect.zw;if(uv.x<0.||uv.x>1.||uv.y<0.||uv.y>1.)discard;float rim=edge>0.?max(1.-smoothstep(edge-.04,edge,uv.x),smoothstep(1.-edge,1.-edge+.04,uv.x)):1.;float mask=step(.02,texture2D(desk,uv).a)*visibility*rim;gl_FragColor=texture2D(hands,v)*mask;}",
        );
      this.program = gl.createProgram();
      gl.attachShader(this.program, vs);
      gl.attachShader(this.program, fs);
      gl.linkProgram(this.program);
      require(gl.getProgramParameter(
        this.program,
        gl.LINK_STATUS,
      ), gl.getProgramInfoLog(this.program));
      gl.deleteShader(vs);
      gl.deleteShader(fs);
      this.buffer = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
      gl.bufferData(
        gl.ARRAY_BUFFER,
        new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]),
        gl.STATIC_DRAW,
      );
      this.position = gl.getAttribLocation(this.program, "a");
      this.hands = gl.getUniformLocation(this.program, "hands");
      this.desk = gl.getUniformLocation(this.program, "desk");
      for (const name of [
        "maskRect",
        "pivot",
        "rotation",
        "visibility",
        "edge",
      ])
        this[name] = gl.getUniformLocation(this.program, name);
    }
    draw(
      model,
      mask,
      rect,
      side,
      visibility = 1,
      rotation = 0,
      pivot = [0, 0],
      edge = 0,
    ) {
      if (visibility <= 0) return;
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.framebuffer);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      model.draw(side ? "foreground-" + side : "foreground", this.framebuffer);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.useProgram(this.program);
      gl.bindBuffer(gl.ARRAY_BUFFER, this.buffer);
      gl.enableVertexAttribArray(this.position);
      gl.vertexAttribPointer(this.position, 2, gl.FLOAT, false, 0, 0);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, this.image);
      gl.uniform1i(this.hands, 0);
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, mask.id);
      gl.uniform1i(this.desk, 1);
      gl.uniform4fv(this.maskRect, rect);
      gl.uniform2fv(this.pivot, pivot);
      gl.uniform1f(this.rotation, rotation);
      gl.uniform1f(this.visibility, visibility);
      gl.uniform1f(this.edge, edge);
      gl.disable(gl.DEPTH_TEST);
      gl.disable(gl.CULL_FACE);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
      gl.activeTexture(gl.TEXTURE0);
    }
    release() {
      gl.deleteTexture(this.image);
      gl.deleteFramebuffer(this.framebuffer);
      gl.deleteBuffer(this.buffer);
      gl.deleteProgram(this.program);
    }
  }
  class Actor {
    async initialize(modelUrl) {
      this.url = local(modelUrl);
      const definition = await json(this.url),
        refs = definition.FileReferences;
      require(refs?.Moc &&
        refs.Textures?.length, "Incomplete model definition");
      const response = await fetch(local(refs.Moc, this.url));
      require(response.ok, "MOC asset unavailable");
      const bytes = await response.arrayBuffer(),
        core = globalThis.Live2DCubismCore;
      require(core.Moc.prototype.hasMocConsistency(bytes) ===
        1, "Official Core rejected model consistency");
      this.moc = core.Moc.fromArrayBuffer(bytes);
      require(this.moc, "Core could not load MOC");
      this.model = core.Model.fromMoc(this.moc);
      require(this.model, "Core could not create model");
      this.neutral = Float32Array.from(this.model.parameters.defaultValues);
      this.indices = new Map(this.model.parameters.ids.map((id, i) => [id, i]));
      this.model.update();
      this.feet = this.model.drawables.ids.flatMap((id, i) =>
        /^ArtMeshFootwear/.test(id)
          ? [
              {
                index: i,
                vertices: Float32Array.from(
                  this.model.drawables.vertexPositions[i],
                ),
              },
            ]
          : [],
      );
      require(this.feet.length >= 2, "Missing rooted foot geometry");
      const required = Object.keys(A.neutral());
      const missing = required.filter((id) => !this.indices.has(id));
      metrics.missingParameters.push(...missing);
      require(missing.length ===
        0, "Model is missing authored action parameters: " + missing.join(","));
      this.wrapper = new framework.CubismModel(this.model);
      this.wrapper.initialize();
      const originalVisible = this.wrapper.getDrawableDynamicFlagIsVisible.bind(
        this.wrapper,
      );
      this.pass = "all";
      const bindings = await json(local("action-bindings.json", this.url));
      const foregroundIds = ["forearm-l", "forearm-r"].map(
        (name) => bindings.drawables?.[name],
      );
      require(foregroundIds.every(
        (id) => typeof id === "string" && this.model.drawables.ids.includes(id),
      ), "Invalid authored foreground bindings");
      this.foreground = new Set(
        this.model.drawables.ids
          .map((id, i) => (foregroundIds.includes(id) ? i : -1))
          .filter((i) => i >= 0),
      );
      require(this.foreground.size >=
        2, "Missing semantic foreground hand drawables");
      this.handAnchors = {};
      this.eyeAnchors = {};
      this.eyelids = {};
      this.foregroundSides = {};
      for (const side of ["r", "l"]) {
        this.handAnchors[side] = this.bindAttachment(
          bindings.handAttachments?.[side],
        );
        this.eyeAnchors[side] = this.bindAttachment(
          bindings.eyeAttachments?.[side],
        );
        this.eyelids[side] = this.model.drawables.ids.indexOf(
          bindings.drawables?.["irides-" + side],
        );
        require(this.eyelids[side] >= 0, "Missing authored eyelid drawable");
        const names = ["forearm-" + side];
        this.foregroundSides[side] = new Set(
          names.map((name) =>
            this.model.drawables.ids.indexOf(bindings.drawables[name]),
          ),
        );
      }
      this.wrapper.getDrawableDynamicFlagIsVisible = (i) =>
        originalVisible(i) &&
        (this.pass === "all" ||
          (this.pass === "foreground"
            ? this.foreground.has(i)
            : this.foregroundSides[this.pass.slice(-1)]?.has(i)));
      this.renderer = new framework.CubismRenderer_WebGL(
        canvas.width,
        canvas.height,
      );
      this.renderer.initialize(this.wrapper);
      this.renderer.startUp(gl);
      this.renderer.setIsPremultipliedAlpha(true);
      const images = await Promise.all(
        refs.Textures.map((path) => texture(local(path, this.url))),
      );
      images.forEach((image, i) => this.renderer.bindTexture(i, image.id));
      const c = this.model.canvasinfo;
      require(c.CanvasWidth === 384 &&
        c.CanvasHeight ===
          346, "Model scene coordinate contract must be 384x346");
      const matrix = new framework.CubismMatrix44();
      matrix.setMatrix(
        new Float32Array([
          (2 * c.PixelsPerUnit) / 384,
          0,
          0,
          0,
          0,
          (2 * c.PixelsPerUnit) / 346,
          0,
          0,
          0,
          0,
          1,
          0,
          (2 * c.CanvasOriginX) / 384 - 1,
          1 - (2 * c.CanvasOriginY) / 346,
          0,
          1,
        ]),
      );
      this.renderer.setMvpMatrix(matrix);
      this.renderer.setRenderState(null, [0, 0, canvas.width, canvas.height]);
      this.renderer.loadShaders("/runtime/shaders/");
      for (
        let i = 0;
        !framework.CubismShaderManager_WebGL.getInstance().getShader(gl)
          ._isShaderLoaded;
        i++
      ) {
        require(i < 200, "Official shader timeout");
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
      gl.bindFramebuffer(gl.FRAMEBUFFER, foregroundTarget.framebuffer);
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      this.draw("all", foregroundTarget.framebuffer);
      const pixels = new Uint8Array(canvas.width * canvas.height * 4);
      gl.readPixels(
        0,
        0,
        canvas.width,
        canvas.height,
        gl.RGBA,
        gl.UNSIGNED_BYTE,
        pixels,
      );
      let visible = 0;
      for (let i = 3; i < pixels.length; i += 4) if (pixels[i] > 8) visible++;
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      require(gl.getError() === gl.NO_ERROR &&
        visible >
          10, "Official Core model produced empty or invalid WebGL output");
      metrics.modelVisiblePixels[this.url] = visible;
      metrics.modelsLoaded++;
      return this;
    }
    update(parameters) {
      const p = this.model.parameters;
      p.values.set(this.neutral);
      for (const [id, value] of Object.entries(parameters)) {
        const i = this.indices.get(id);
        if (i !== undefined)
          p.values[i] = Math.max(
            p.minimumValues[i],
            Math.min(p.maximumValues[i], value),
          );
      }
      this.model.drawables.resetDynamicFlags();
      this.model.update();
      for (const vertices of this.model.drawables.vertexPositions)
        for (const v of vertices)
          if (!Number.isFinite(v)) metrics.nonFiniteVertices++;
      for (const foot of this.feet) {
        const vertices = this.model.drawables.vertexPositions[foot.index];
        for (let i = 0; i < vertices.length; i++)
          metrics.maximumFootDelta = Math.max(
            metrics.maximumFootDelta,
            Math.abs(vertices[i] - foot.vertices[i]),
          );
      }
      require(metrics.nonFiniteVertices === 0, "Non-finite Core vertex");
      metrics.coreUpdates++;
    }
    draw(pass, framebuffer = null) {
      this.pass = pass;
      this.renderer.setRenderState(framebuffer, [
        0,
        0,
        canvas.width,
        canvas.height,
      ]);
      this.renderer.drawModel();
      metrics.drawPasses++;
    }
    bindAttachment(attachment) {
      require(attachment?.source?.length === 2, "Missing authored attachment");
      const drawable = this.model.drawables.ids.indexOf(attachment.drawableId);
      require(drawable >= 0, "Missing attachment mesh");
      const c = this.model.canvasinfo;
      return {
        drawable,
        ...G.bindPoint(
          this.model.drawables.vertexPositions[drawable],
          this.model.drawables.indices[drawable],
          [
            (attachment.source[0] - c.CanvasOriginX) / c.PixelsPerUnit,
            (c.CanvasOriginY - attachment.source[1]) / c.PixelsPerUnit,
          ],
        ),
      };
    }
    attachment(a) {
      const p = this.model.drawables.vertexPositions[a.drawable],
        c = this.model.canvasinfo,
        point = G.point(p, a);
      return {
        x: point.x * c.PixelsPerUnit + c.CanvasOriginX,
        y: c.CanvasOriginY - point.y * c.PixelsPerUnit,
      };
    }
    hand(side) {
      return this.attachment(this.handAnchors[side]);
    }
    eye(side) {
      return this.attachment(this.eyeAnchors[side]);
    }
    eyelid(side) {
      const vertices = this.model.drawables.vertexPositions[this.eyelids[side]],
        c = this.model.canvasinfo;
      let minX = Infinity,
        maxX = -Infinity,
        minY = Infinity,
        maxY = -Infinity;
      for (let i = 0; i < vertices.length; i += 2) {
        minX = Math.min(minX, vertices[i]);
        maxX = Math.max(maxX, vertices[i]);
        minY = Math.min(minY, vertices[i + 1]);
        maxY = Math.max(maxY, vertices[i + 1]);
      }
      return {
        x: ((minX + maxX) / 2) * c.PixelsPerUnit + c.CanvasOriginX,
        y: c.CanvasOriginY - ((minY + maxY) / 2) * c.PixelsPerUnit,
      };
    }
    release() {
      if (this.released) return;
      this.released = true;
      this.renderer?.release();
      this.model?.release();
      this.moc?._release();
      metrics.modelsReleased++;
    }
  }
  async function actor(url) {
    url = local(url);
    if (!models.has(url)) {
      const instance = new Actor();
      models.set(
        url,
        instance.initialize(url).catch((error) => {
          instance.release();
          models.delete(url);
          throw error;
        }),
      );
    }
    return models.get(url);
  }
  async function prepare(equipment) {
    const key = B.equipmentKey(equipment);
    if (prepared.has(key)) return prepared.get(key);
    const desk = catalog.desks.find((x) => x.id === equipment.desk),
      computer = catalog.computers.find((x) => x.id === equipment.computer),
      outfit = config.outfits[equipment.outfit];
    require(desk && computer && outfit, "Unknown or unavailable equipment");
    const pending = Promise.all([
      actor(outfit.modelUrl),
      texture(asset(desk.back)),
      texture(asset(desk.front)),
      texture(asset(computer.image)),
    ]).then(([model, back, front, pc]) => ({
      equipment: { ...equipment },
      model,
      back,
      front,
      pc,
    }));
    prepared.set(key, pending);
    try {
      return await pending;
    } catch (error) {
      prepared.delete(key);
      throw error;
    }
  }
  const extras = {};
  let fire = [];
  function decorations() {
    for (const name of ["tears", "cryEye", "anger"]) {
      const source = document.createElement("canvas");
      source.width = 64;
      source.height = name === "cryEye" ? 8 : 64;
      const c = source.getContext("2d");
      if (name === "cryEye") {
        c.fillStyle = "#433329";
        c.beginPath();
        c.roundRect(0, 0, 64, 8, 4);
        c.fill();
      } else if (name === "tears") {
        c.fillStyle = "#79c8ed";
        c.beginPath();
        c.moveTo(0, 0);
        c.lineTo(64, 0);
        c.lineTo(64, 57);
        c.quadraticCurveTo(64, 64, 32, 64);
        c.quadraticCurveTo(0, 64, 0, 57);
        c.closePath();
        c.fill();
      } else {
        c.strokeStyle = "#e84533";
        c.lineWidth = 7;
        c.lineCap = "round";
        for (let i = 0; i < 4; i++) {
          c.save();
          c.translate(32, 32);
          c.rotate((i * Math.PI) / 2);
          c.beginPath();
          c.moveTo(-6, -23);
          c.lineTo(-6, -11);
          c.quadraticCurveTo(-6, -6, -11, -6);
          c.lineTo(-23, -6);
          c.stroke();
          c.restore();
        }
      }
      const id = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, id);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.texImage2D(
        gl.TEXTURE_2D,
        0,
        gl.RGBA,
        gl.RGBA,
        gl.UNSIGNED_BYTE,
        source,
      );
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      const image = { id, width: source.width, height: source.height };
      textures.set("decoration:" + name, Promise.resolve(image));
      extras[name] = {
        image,
        rect: name === "tears" ? [0, 0, 12, 24] : [238, 112, 23, 23],
      };
    }
  }
  function extra(name, alpha, dx = 0, dy = 0, scale = 1) {
    const item = extras[name];
    if (!item || alpha <= 0) return;
    const r = item.rect,
      w = r[2] * scale,
      h = r[3] * scale;
    quad.draw(
      item.image,
      [r[0] + dx + (r[2] - w) / 2, r[1] + dy + (r[3] - h), w, h],
      alpha,
    );
  }
  function draw(now) {
    if (dead || !active || !current) return;
    const p = B.progress(current, receivedAt, now),
      sample = A.sample(
        neutralizing ? "Idle" : current.kind,
        neutralizing ? 0 : p,
      ),
      motion = A.scene(
        neutralizing ? "Idle" : current.kind,
        neutralizing ? 0 : p,
        scene,
      ),
      m = active.model;
    let neutralProgress = 0;
    if (neutralizing) {
      neutralProgress = B.transitionProgress(
        neutralizing.clock,
        now,
        neutralizing.request.paused,
      );
      const blend = A.smooth(neutralProgress);
      for (const id of Object.keys(sample.parameters))
        sample.parameters[id] =
          neutralizing.from[id] +
          (sample.parameters[id] - neutralizing.from[id]) * blend;
    } else if (transitionFrom && current.phase !== "Terminal") {
      const blend = A.smooth(
        B.transitionProgress(transitionClock, now, current.paused),
      );
      for (const id of Object.keys(sample.parameters))
        if (id !== "ParamBusyEyes")
          sample.parameters[id] =
            transitionFrom[id] +
            (sample.parameters[id] - transitionFrom[id]) * blend;
      if (blend === 1) transitionFrom = null;
    }
    if (
      !neutralizing &&
      current.paused &&
      current.phase !== "Terminal" &&
      frozenParameters
    )
      Object.assign(sample.parameters, frozenParameters);
    lastParameters = { ...sample.parameters };
    m.update(sample.parameters);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    if (motion.fire > 0) {
      const index =
          Math.floor(p * A.durations[current.kind] * scene.fire.sampleRate) %
          fire.length,
        g = motion.fire,
        anchor = scene.fire.anchor.y;
      gl.enable(gl.SCISSOR_TEST);
      gl.scissor(
        0,
        Math.ceil(((346 - scene.deskSurfaceAnchor.y) / 346) * canvas.height),
        canvas.width,
        canvas.height,
      );
      quad.draw(fire[index], [0, anchor * (1 - g), 384, 346 * g]);
      gl.disable(gl.SCISSOR_TEST);
    }
    m.draw("all");
    const left = m.hand("l"),
      right = m.hand("r"),
      lift = sample.props.bowl;
    if (motion.visible) {
      quad.draw(active.back, [motion.deskX, 0, 384, 346]);
      for (const [side, hand] of [
        ["l", left],
        ["r", right],
      ])
        foregroundTarget.draw(
          m,
          active.back,
          [motion.deskX, 0, 384, 346],
          side,
          G.aboveDesk(hand.y, scene.deskSurfaceAnchor.y),
        );
    }
    const bowlName = current.kind === "Eat" ? "bowlFull" : "bowlEmpty",
      bowl = extras[bowlName],
      bowlAnchor =
        current.kind === "Eat"
          ? { x: (left.x + right.x) / 2, y: (left.y + right.y) / 2 }
          : {
              x: scene.deskSurfaceAnchor.x + motion.deskX,
              y: scene.deskSurfaceAnchor.y,
            },
      bowlRect = G.heldRect(
        bowl.rect.slice(2),
        bowlAnchor,
        current.kind === "Eat" ? [0.5, 0.58] : [0.5, 1],
        0.9,
      );
    quad.draw(bowl.image, bowlRect, lift);
    if (current.kind === "Eat" && lift > 0)
      foregroundTarget.draw(
        m,
        bowl.image,
        bowlRect,
        null,
        lift,
        0,
        [0, 0],
        0.22,
      );
    const tea = extras.tea,
      teaPivot = [right.x, right.y],
      teaRect = G.heldRect(tea.rect.slice(2), right, [0.25, 0.72], 0.9);
    quad.draw(
      tea.image,
      teaRect,
      sample.props.tea,
      sample.props.teaTilt,
      teaPivot,
    );
    if (sample.props.tea > 0)
      foregroundTarget.draw(
        m,
        tea.image,
        teaRect,
        "r",
        sample.props.tea,
        sample.props.teaTilt,
        teaPivot,
        0.26,
      );
    metrics.attachments = { left, right, bowlRect, teaRect };
    for (const side of ["r", "l"]) {
      const eye = m.eyelid(side),
        tear = G.tear(eye, sample.props.tearFlow, side, sample.props.tears);
      quad.draw(
        extras.tears.image,
        tear.rect,
        sample.props.tears * tear.opacity,
      );
      quad.draw(
        extras.cryEye.image,
        [eye.x - 10, eye.y - 1.5, 20, 3],
        sample.props.tears,
      );
    }
    extra(
      "anger",
      sample.props.anger,
      0,
      0,
      1 + 0.05 * Math.sin(p * Math.PI * 12),
    );
    if (motion.visible) {
      quad.draw(active.front, [motion.deskX, 0, 384, 346]);
      if (motion.computer)
        quad.draw(active.pc, [0, motion.computerY, 384, 346]);
    }
    const error = gl.getError();
    if (error !== gl.NO_ERROR) metrics.glErrors.push(error);
    require(error === gl.NO_ERROR, "WebGL frame failed: " + error);
    metrics.frames++;
    drawRecordingFrame();
    if (neutralizing) {
      if (neutralProgress === 1 && !neutralizing.acknowledged) {
        neutralizing.firstFrame ??= frameSerial;
        if (frameSerial - neutralizing.firstFrame >= 2) {
          neutralizing.acknowledged = true;
          const request = neutralizing.request;
          post({
            type: "neutralized",
            requestId: request.requestId,
            revision: request.revision,
            sequence: request.sequence,
            kind: request.kind,
            equipment: { ...request.equipment },
          });
        }
      }
      return;
    }
    const key = B.equipmentKey(active.equipment);
    if (presentedKey !== key) {
      if (presentedCandidate?.key !== key)
        presentedCandidate = {
          key,
          firstFrame: frameSerial,
          revision: current.revision,
          sequence: current.sequence,
          kind: current.kind,
          equipment: { ...active.equipment },
        };
      else if (frameSerial - presentedCandidate.firstFrame >= 2) {
        presentedKey = key;
        const c = presentedCandidate;
        post({
          type: "presented",
          revision: c.revision,
          sequence: c.sequence,
          kind: c.kind,
          equipment: c.equipment,
        });
      }
    }
    const terminal = [current.sequence, current.kind].join(":");
    if (current.phase === "Terminal" && p === 1 && terminalKey !== terminal) {
      if (terminalCandidate?.key !== terminal)
        terminalCandidate = { key: terminal, firstFrame: frameSerial };
      else if (frameSerial - terminalCandidate.firstFrame >= 2) {
        terminalKey = terminal;
        metrics.terminal.push(terminal);
        post({
          type: "terminal",
          sequence: current.sequence,
          kind: current.kind,
        });
      }
    }
    if (qa) {
      document.getElementById("progress").value = p;
      document.getElementById("status").textContent =
        current.kind +
        " · " +
        p.toFixed(3) +
        " · official Core / " +
        metrics.frames +
        " frames";
    }
  }
  async function receive(message) {
    if (dead) return;
    require(message?.protocol === 1 &&
      message.generation === generation, "Message generation mismatch");
    if (message.type === "record-raster-frame") {
      if (!rasterFrames.size) return;
      if (message.key === null) rasterFrame = null;
      else {
        require(Number.isSafeInteger(message.index) &&
          message.index >= 0, "Invalid recorded raster frame index");
        const frames = rasterFrames.get(message.key);
        require(frames &&
          message.index < frames.length, "Unknown recorded raster frame");
        rasterFrame = frames[message.index];
      }
      draw(performance.now());
      return;
    }
    if (message.type === "neutralize") {
      const next = B.neutralize(
        message,
        generation,
        neutralizeRequest,
        bridgeRevision,
      );
      if (!next) return;
      require(active &&
        current &&
        B.equipmentKey(next.equipment) ===
          B.equipmentKey(
            active.equipment,
          ), "Neutralize requires the currently presented equipment");
      const now = performance.now();
      if (neutralizing?.request.requestId === next.requestId) {
        B.transitionProgress(neutralizing.clock, now, next.paused);
        neutralizing.request = next;
      } else {
        neutralizing = {
          request: next,
          from: { ...(lastParameters ?? A.neutral()) },
          clock: { startedAt: now, pausedAt: next.paused ? now : undefined },
        };
      }
      neutralizeRequest = next;
      bridgeRevision = next.revision;
      terminalCandidate = null;
      draw(now);
      return;
    }
    if (message.type === "prepare") {
      const revision = ++prepareRevision;
      try {
        const selection = await prepare(message.equipment);
        if (revision !== prepareRevision || dead) return;
        post({
          type: "prepared",
          requestId: message.requestId,
          equipment: selection.equipment,
        });
      } catch (error) {
        post({
          type: "prepare-error",
          requestId: message.requestId,
          message: String(error.message),
        });
      }
      return;
    }
    if (message.type !== "state") return;
    let next = B.state(message, generation, { revision: bridgeRevision });
    if (!next) return;
    const pending = prepared.get(B.equipmentKey(next.equipment));
    require(pending, "State attempted unprepared equipment");
    const selection = await pending;
    if (next.revision <= bridgeRevision) return;
    const now = performance.now();
    next = B.align(next, current, receivedAt, now);
    const changed =
      current &&
      (next.sequence !== current.sequence || next.kind !== current.kind);
    if (!changed && current && next.paused && !current.paused)
      frozenParameters = lastParameters;
    if (changed || !next.paused) frozenParameters = null;
    if (changed) {
      terminalKey = "";
      terminalCandidate = null;
      transitionFrom = next.paused ? null : lastParameters;
      transitionClock = { startedAt: now };
    }
    active = selection;
    neutralizing = null;
    bridgeRevision = next.revision;
    current = next;
    receivedAt = now;
    draw(receivedAt);
  }
  function loop(now) {
    if (dead) return;
    try {
      if (lastTime !== undefined && metrics.intervals.length < 20000)
        metrics.intervals.push(now - lastTime);
      lastTime = now;
      frameSerial++;
      draw(now);
      raf = requestAnimationFrame(loop);
    } catch (error) {
      fail(error);
    }
  }
  function qaState(kind, progress = 0, paused = false) {
    return receive({
      protocol: 1,
      generation,
      type: "state",
      revision: ++qaRevision,
      sequence: qaRevision,
      kind,
      phase: progress === 1 ? "Terminal" : kind === "Idle" ? "Idle" : "Playing",
      progress,
      durationSeconds: A.durations[kind],
      paused,
      equipment: active.equipment,
    });
  }
  async function recordStart(fps = 30) {
    require(!recording, "Recording already running");
    require(fps === 30 || fps === 60, "Unsupported capture rate");
    require(typeof MediaRecorder !== "undefined" &&
      MediaRecorder.isTypeSupported(
        "video/webm;codecs=vp9",
      ), "WebM capture is unavailable");
    const surface = document.createElement("canvas");
    surface.width = canvas.width;
    surface.height = canvas.height;
    const stream = surface.captureStream(fps),
      chunks = [],
      recorder = new MediaRecorder(stream, {
        mimeType: "video/webm;codecs=vp9",
        videoBitsPerSecond: 2500000,
      });
    let bytes = 0,
      overflow = false;
    const done = new Promise((resolve, reject) => {
      recorder.ondataavailable = (e) => {
        bytes += e.data.size;
        if (bytes > 32 * 1024 * 1024) {
          overflow = true;
          if (recorder.state === "recording") recorder.stop();
        } else chunks.push(e.data);
      };
      recorder.onerror = (e) => reject(e.error);
      recorder.onstop = async () => {
        stream.getTracks().forEach((t) => t.stop());
        if (overflow) {
          reject(new Error("Canvas recording exceeds 32 MiB"));
          return;
        }
        const data = new Uint8Array(
          await new Blob(chunks, { type: recorder.mimeType }).arrayBuffer(),
        );
        let text = "";
        for (let i = 0; i < data.length; i += 8192)
          text += String.fromCharCode(...data.subarray(i, i + 8192));
        resolve(btoa(text));
      };
    });
    recorder.start(1000);
    recording = { recorder, done, surface, context: surface.getContext("2d") };
    draw(performance.now());
  }
  async function prepareRasterRecording(sources) {
    require(!recording, "Prepare raster recording before capture starts");
    const preparation = ++rasterPreparation,
      result = new Map();
    let count = 0,
      pixels = 0;
    rasterFrames = new Map();
    rasterFrame = null;
    for (const [key, urls] of Object.entries(sources)) {
      require(["Idle", "Yawn", "Eat"].includes(key) &&
        Array.isArray(urls), "Invalid raster recording clip");
      count += urls.length;
      require(count <= 512, "Raster recording frame limit exceeded");
      const frames = [];
      for (const url of urls) {
        require(typeof url === "string" &&
          url.startsWith("data:image/png;base64,") &&
          url.length <=
            2 *
              1024 *
              1024, "Only bounded local PNG snapshots can be recorded");
        const image = new Image();
        image.src = url;
        await image.decode();
        pixels += image.width * image.height;
        require(pixels <=
          32 * 1024 * 1024, "Raster recording decoded memory limit exceeded");
        frames.push(image);
      }
      result.set(key, frames);
    }
    if (preparation === rasterPreparation) rasterFrames = result;
    return { clips: [...result.keys()], frames: count };
  }
  function drawRecordingFrame() {
    if (!recording) return;
    const { surface, context } = recording,
      image = rasterFrame || canvas,
      scale = Math.min(
        surface.width / image.width,
        surface.height / image.height,
      ),
      width = image.width * scale,
      height = image.height * scale;
    context.clearRect(0, 0, surface.width, surface.height);
    context.drawImage(
      image,
      (surface.width - width) / 2,
      (surface.height - height) / 2,
      width,
      height,
    );
  }
  async function recordStop() {
    require(recording, "No recording");
    const saved = recording;
    recording = null;
    rasterFrames = new Map();
    rasterFrame = null;
    rasterPreparation++;
    if (saved.recorder.state === "recording") saved.recorder.stop();
    return saved.done;
  }
  async function start() {
    require(Number.isSafeInteger(generation) &&
      generation >= 0, "Missing host generation");
    config = await json("config.json");
    catalog = await json(config.sceneCatalogUrl);
    scene = catalog.scenes[0];
    require(scene?.canvas.width === 384 &&
      scene.canvas.height === 346, "Invalid scene contract");
    gl = canvas.getContext("webgl", {
      alpha: true,
      premultipliedAlpha: true,
      antialias: true,
      preserveDrawingBuffer: qa,
    });
    require(gl, "WebGL is unavailable");
    canvas.addEventListener("webglcontextlost", (event) => {
      event.preventDefault();
      fail(new Error("WebGL context lost; host must recreate renderer"));
    });
    const core = globalThis.Live2DCubismCore;
    require(core, "Official Core unavailable");
    for (let i = 0; ; i++) {
      try {
        core.Version.csmGetVersion();
        break;
      } catch (error) {
        if (i >= 100) throw error;
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
    }
    framework = globalThis.EagleFramework || globalThis.ProbeFramework;
    require(framework, "Official Framework bundle unavailable");
    framework.CubismFramework.startUp();
    framework.CubismFramework.initialize();
    quad = new Quads();
    foregroundTarget = new ForegroundTarget();
    require(config.outfits &&
      Object.keys(config.outfits).length ===
        6, "Complete six-outfit model pack required");
    require(new Set(
      Object.values(config.outfits).map((item) => local(item.modelUrl)),
    ).size === 6, "Each outfit requires its own authored model asset");
    await Promise.all(
      Object.entries(config.extras ?? {}).map(async ([name, item]) => {
        extras[name] = {
          rect: item.rect ?? [0, 0, 384, 346],
          image: await texture(local(item.url)),
        };
      }),
    );
    require(extras.bowlEmpty &&
      extras.bowlFull &&
      extras.tea, "Required bowl / tea props missing");
    decorations();
    fire = await Promise.all(
      Array.from({ length: scene.fire.loopFrameCount }, (_, i) =>
        texture(
          asset(
            scene.fire.directory +
              "/frame-" +
              String(i).padStart(4, "0") +
              ".png",
          ),
        ),
      ),
    );
    await Promise.all([
      ...Object.values(config.outfits).map((item) => actor(item.modelUrl)),
      ...catalog.desks.flatMap((desk) => [
        texture(asset(desk.back)),
        texture(asset(desk.front)),
      ]),
      ...catalog.computers.map((computer) => texture(asset(computer.image))),
    ]);
    const initial = config.defaultEquipment ?? {
      desk: catalog.defaultSelection.deskId,
      computer: catalog.defaultSelection.computerId,
      outfit: Object.keys(config.outfits)[0],
    };
    active = await prepare(initial);
    require(metrics.modelsLoaded ===
      6, "All six outfit models must be initialized before ready");
    const capabilities = {
      backend: "live2d-core",
      clips: Object.keys(A.durations),
      desks: catalog.desks.map((x) => x.id),
      computers: catalog.computers.map((x) => x.id),
      outfits: Object.keys(config.outfits),
      parameters: Array.from(active.model.model.parameters.ids),
    };
    window.chrome?.webview?.addEventListener("message", (event) =>
      receive(event.data).catch(fail),
    );
    post({ type: "ready", capabilities });
    if (qa) {
      document.body.classList.add("qa");
      document.getElementById("qa").hidden = false;
      const select = document.getElementById("actions");
      for (const kind of Object.keys(A.durations)) {
        const option = document.createElement("option");
        option.value = option.textContent = kind;
        select.append(option);
      }
      select.onchange = () => qaState(select.value, 0, true);
      document.getElementById("play").onclick = () => qaState(select.value);
      document.getElementById("progress").oninput = (event) =>
        qaState(select.value, Number(event.target.value), true);
      document.getElementById("record").onclick = async () => {
        await recordStart();
        await qaState(select.value);
        setTimeout(
          async () => {
            const data = await recordStop(),
              a = document.createElement("a");
            a.href = "data:video/webm;base64," + data;
            a.download = select.value + ".webm";
            a.click();
          },
          A.durations[select.value] * 1000 + 100,
        );
      };
      await qaState("Idle");
    }
    const report = () => ({
      ...metrics,
      actualDraws: metrics.frames,
      active: active?.equipment,
      loadedModel: active?.model.url,
      kind: current?.kind,
      progress: current
        ? B.progress(current, receivedAt, performance.now())
        : null,
      lastSequence: current?.sequence,
      parameters: lastParameters ? { ...lastParameters } : null,
      recordingSurface: recording ? (rasterFrame ? "Png" : "Live2D") : null,
      rasterFramesPrepared: [...rasterFrames.values()].reduce(
        (count, frames) => count + frames.length,
        0,
      ),
      neutralizing: neutralizing
        ? {
            requestId: neutralizing.request.requestId,
            acknowledged: !!neutralizing.acknowledged,
          }
        : null,
      renderer: "Official Cubism Core / Framework WebGL",
      note: "rAF callbacks and video capture rate are not physical display Present measurements",
    });
    window.eagleStage = {
      capabilities,
      sample: (kind, progress) => qaState(kind, progress, true),
      play: (kind) => qaState(kind),
      receive,
      report,
      diagnostics: report,
      capture: () => {
        draw(performance.now());
        return canvas.toDataURL("image/png");
      },
      beginRecording: recordStart,
      finishRecording: recordStop,
      prepareRasterRecording,
    };
    raf = requestAnimationFrame(loop);
  }
  start().catch(fail);
  window.addEventListener("beforeunload", () => {
    dead = true;
    if (raf) cancelAnimationFrame(raf);
    if (recording) {
      recording.recorder.stop();
      recording = null;
    }
    for (const item of models.values())
      item.then((model) => model.release()).catch(() => {});
    for (const item of textures.values())
      item.then((t) => gl.deleteTexture(t.id)).catch(() => {});
    foregroundTarget?.release();
    quad?.release();
  });
})();
