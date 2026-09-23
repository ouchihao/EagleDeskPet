"use strict";
(() => {
  const core = window.Live2DCubismCore;
  const canvas = document.getElementById("stage"), status = document.getElementById("status");
  const result = { loaded: false, consistency: false, coreCreated: false, nonFinite: 0, glErrors: [], runtimeErrors: [], samples: [], coreLog: [], parameterSweeps: [] };
  let model, moc, gl, renderer, neutral, reference, running = false, phase = 0, lastTime, intervals = [], frames = 0, inputs = new Map(), config, video, raf, samplingStart;
  function runtimeError(error){if(result.runtimeErrors.length<20)result.runtimeErrors.push(String(error?.stack||error).slice(0,2000));running=false;}
  window.addEventListener("error",event=>runtimeError(event.error||event.message));
  window.addEventListener("unhandledrejection",event=>runtimeError(event.reason));
  function require(condition, message) { if (!condition) throw new Error(message); }
  function finite(array) { let bad = 0; for (const x of array) if (!Number.isFinite(x)) bad++; result.nonFinite += bad; return bad === 0; }
  function snapshot() { return model.drawables.vertexPositions.map((positions, i) => ({ positions: Float32Array.from(positions), opacity: model.drawables.opacities[i] })); }
  function differences(base) {
    let maximumVertexDelta = 0, maximumOpacityDelta = 0, changedDrawables = [], drawableDeltas = [];
    model.drawables.vertexPositions.forEach((p, i) => {
      finite(p); let delta = 0;
      for (let j = 0; j < p.length; j++) delta = Math.max(delta, Math.abs(p[j] - base[i].positions[j]));
      const opacity = Math.abs(model.drawables.opacities[i] - base[i].opacity);
      maximumVertexDelta = Math.max(maximumVertexDelta, delta); maximumOpacityDelta = Math.max(maximumOpacityDelta, opacity);
      if (delta > 1e-7 || opacity > 1e-7) changedDrawables.push(model.drawables.ids[i]);
      const h=ProbeAudit.bounds(base[i].positions).height;
      drawableDeltas.push({id:model.drawables.ids[i], maximumVertexDelta:delta, maximumOpacityDelta:opacity,heightRatio:h>0?ProbeAudit.bounds(p).height/h:1});
    });
    finite(model.drawables.opacities); finite(model.parameters.values);
    return { maximumVertexDelta, maximumOpacityDelta, changedDrawables, drawableDeltas };
  }
  function reset() { model.parameters.values.set(neutral); }
  function set(id, value) {
    const i = model.parameters.ids.indexOf(id);
    if (i < 0) return false;
    model.parameters.values[i] = Math.min(model.parameters.maximumValues[i], Math.max(model.parameters.minimumValues[i], value));
    return true;
  }
  function edge(id, high) {
    const i = model.parameters.ids.indexOf(id); if (i >= 0) set(id, high ? model.parameters.maximumValues[i] : model.parameters.minimumValues[i]);
  }
  function update() { model.drawables.resetDynamicFlags(); model.update(); differences(reference); renderer.draw(); }
  function readPixels() {
    const pixels = new Uint8Array(canvas.width * canvas.height * 4);
    gl.readPixels(0, 0, canvas.width, canvas.height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
    let visiblePixels = 0, alphaSum = 0;
    for (let i = 3; i < pixels.length; i += 4) { if (pixels[i] > 8) visiblePixels++; alphaSum += pixels[i]; }
    return { visiblePixels, meanAlpha: alphaSum / (canvas.width * canvas.height), glError: gl.getError() };
  }
  function sweep() {
    for (let i = 0; i < model.parameters.count; i++) {
      let states = [];
      for (const value of [model.parameters.minimumValues[i], model.parameters.defaultValues[i], model.parameters.maximumValues[i]]) {
        reset(); model.parameters.values[i] = value; model.update();
        states.push({ value, ...differences(reference) });
      }
      result.parameterSweeps.push({ id: model.parameters.ids[i], states,
        effective: states.some(s => s.maximumVertexDelta > 1e-7 || s.maximumOpacityDelta > 1e-7) });
    }
    reset(); model.update();
  }
  function pose(name) {
    running = false; reset();
    if (name === "eyes-closed") { edge("ParamEyeLOpen", false); edge("ParamEyeROpen", false); }
    if (name === "eyes-half") { set("ParamEyeLOpen", 0.5); set("ParamEyeROpen", 0.5); }
    if (name === "breath-high") edge("ParamBreath", true);
    if (name === "head-left") edge("ParamAngleX", false);
    if (name === "head-right") edge("ParamAngleX", true);
    if (name === "mouth-open") edge("ParamMouthOpenY", true);
    update(); refreshControls();
    const sample = { name, ...differences(reference), ...readPixels() };
    result.samples.push(sample); status.textContent = result.name + " / " + name;
    return sample;
  }
  function refreshControls() {
    model.parameters.ids.forEach((id, i) => { const input = inputs.get(id); if (input) { input.value = model.parameters.values[i]; input.nextElementSibling.textContent = Number(input.value).toFixed(3); } });
  }
  function loop(time) {
    if (lastTime !== undefined) intervals.push(Math.min(10000, Math.max(0, time - lastTime)));
    const delta = lastTime === undefined ? 0 : Math.max(0, Math.min(0.1, (time - lastTime) / 1000)); lastTime = time;
    if (running) {
      phase += delta; reset();
      set("ParamBreath", (Math.sin(phase * 2) + 1) * 0.5);
      set("ParamAngleX", Math.sin(phase * 1.05) * 20); set("ParamAngleY", Math.sin(phase * 0.8) * 10); set("ParamAngleZ", Math.sin(phase * 0.7) * 8);
      const blink = Math.pow(Math.max(0, Math.cos(phase * 2.3)), 18);
      set("ParamEyeLOpen", 1 - blink); set("ParamEyeROpen", 1 - blink);
      update(); frames++; if (frames % 12 === 0) refreshControls();
    }
    if (intervals.length > 10000) intervals.shift();
    raf=requestAnimationFrame(loop);
  }
  function report() {
    const sorted = intervals.slice().sort((a, b) => a - b);
    const effectiveIds = result.parameterSweeps.filter(p => p.effective).map(p => p.id);
    const required = ["ParamEyeLOpen", "ParamEyeROpen", "ParamBreath", "ParamAngleX"];
    const eagleContract=config.eagleContract?ProbeAudit.eagle(result.parameterSweeps,model.drawables.ids):null;
    const sampleElapsedSeconds=samplingStart===undefined?0:(performance.now()-samplingStart)/1000;
    const animationSamplePassed=frames>0&&phase>=config.sampleSeconds*0.9&&sampleElapsedSeconds>=config.sampleSeconds*0.9;
    return { ...result, eagleContract, effectiveIds, missingOrInertRequired: required.filter(id => !effectiveIds.includes(id)),
      passed: result.consistency && result.coreCreated && result.nonFinite === 0 && result.glErrors.length === 0 && result.runtimeErrors.length===0 && animationSamplePassed &&
        result.samples.length>0&&result.samples.every(s => s.visiblePixels > 10 && s.glError === 0) && required.every(id => effectiveIds.includes(id))&&(!eagleContract||eagleContract.passed),
      animation: { samplePassed:animationSamplePassed, requestedSeconds:config.sampleSeconds,sampleElapsedSeconds,renderedFrames: frames, callbackCount: intervals.length, meanIntervalMs: intervals.reduce((a,b)=>a+b,0) / Math.max(1, intervals.length),
        p95IntervalMs: sorted[Math.floor(sorted.length * 0.95)] ?? null, elapsedAnimationSeconds: phase, note: "requestAnimationFrame sampling, NOT measured physical display FPS" } };
  }
  function beginRecording() {
    require(typeof MediaRecorder!=="undefined"&&MediaRecorder.isTypeSupported("video/webm;codecs=vp9"),"WebM recording unsupported");
    const stream=canvas.captureStream(30),chunks=[],recorder=new MediaRecorder(stream,{mimeType:"video/webm;codecs=vp9",videoBitsPerSecond:1200000});
    const done=new Promise((resolve,reject)=>{recorder.ondataavailable=e=>chunks.push(e.data);recorder.onerror=e=>reject(new Error(String(e.error)));recorder.onstop=async()=>{stream.getTracks().forEach(t=>t.stop());const blob=new Blob(chunks,{type:recorder.mimeType});if(blob.size>16*1024*1024){reject(new Error("Recording exceeds 16 MiB"));return;}const data=new Uint8Array(await blob.arrayBuffer());let text="";for(let i=0;i<data.length;i+=8192)text+=String.fromCharCode(...data.subarray(i,i+8192));resolve(btoa(text));};});
    recorder.start();video={recorder,done,stream};return true;
  }
  async function finishRecording(){require(video,"Recording was not started");video.recorder.stop();return await video.done;}
  class OfficialRenderer {
    constructor(images) { this.images = images; }
    async initialize() {
      const f=window.ProbeFramework; require(f,"Local official Framework bundle unavailable");
      f.CubismFramework.startUp(); f.CubismFramework.initialize();
      this.wrapper=new f.CubismModel(model); this.wrapper.initialize();
      gl=canvas.getContext("webgl",{alpha:true,premultipliedAlpha:true,antialias:true,preserveDrawingBuffer:true});
      require(gl,"Real WebGL context unavailable");
      result.webgl={version:gl.getParameter(gl.VERSION),renderer:gl.getParameter(gl.RENDERER),maxTextureSize:gl.getParameter(gl.MAX_TEXTURE_SIZE)};
      result.renderer="Official Cubism SDK Web 5-r.5 Framework CubismRenderer_WebGL";
      result.maskedDrawables=Array.from(model.drawables.maskCounts).filter(n=>n>0).length;
      this.native=new f.CubismRenderer_WebGL(canvas.width,canvas.height);
      this.native.initialize(this.wrapper);this.native.startUp(gl);this.native.setIsPremultipliedAlpha(true);
      this.textures=this.images.map((image,i)=>{
        require(image.width<=gl.getParameter(gl.MAX_TEXTURE_SIZE)&&image.height<=gl.getParameter(gl.MAX_TEXTURE_SIZE),"Texture exceeds device limit");
        const t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL,true);gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,false);
        gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,image);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);this.native.bindTexture(i,t);return t;
      });
      let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;
      for(const p of model.drawables.vertexPositions)for(let j=0;j<p.length;j+=2){minX=Math.min(minX,p[j]);maxX=Math.max(maxX,p[j]);minY=Math.min(minY,p[j+1]);maxY=Math.max(maxY,p[j+1]);}
      require([minX,minY,maxX,maxY].every(Number.isFinite)&&maxX>minX&&maxY>minY,"Invalid model bounds");
      result.neutralBounds={minX,minY,maxX,maxY};
      const scale=1.75/Math.max(maxX-minX,maxY-minY), matrix=new f.CubismMatrix44();
      matrix.setMatrix(new Float32Array([scale,0,0,0,0,scale,0,0,0,0,1,0,-(minX+maxX)*scale/2,-(minY+maxY)*scale/2,0,1]));
      this.native.setMvpMatrix(matrix);this.native.setRenderState(null,[0,0,canvas.width,canvas.height]);
      this.native.loadShaders("sdk/shaders/");
      for(let i=0;!f.CubismShaderManager_WebGL.getInstance().getShader(gl)._isShaderLoaded;i++){require(i<300,"Official shader load timeout");await new Promise(r=>setTimeout(r,50));}
    }
    draw(){gl.bindFramebuffer(gl.FRAMEBUFFER,null);gl.viewport(0,0,canvas.width,canvas.height);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);this.native.setRenderState(null,[0,0,canvas.width,canvas.height]);this.native.drawModel();const error=gl.getError();if(error!==gl.NO_ERROR&&result.glErrors.length<20)result.glErrors.push(error);}
    release(){try{this.native?.release();}catch(error){runtimeError(error);}finally{this.native=null;}for(const t of this.textures??[]){try{gl?.deleteTexture(t);}catch(error){runtimeError(error);}}this.textures=[];}
  }
  async function start() {
    require(core,"Official Core script failed to load");
    let attempts=0;
    while(true){try{result.coreVersion=core.Version.csmGetVersion();break;}catch(error){if(++attempts>100)throw error;await new Promise(r=>setTimeout(r,50));}}
    core.Logging.csmSetLogFunction(message=>{if(result.coreLog.length<100)result.coreLog.push(String(message).slice(0,500));});
    config=await(await fetch("config.json")).json();result.name=config.name;
    const response=await fetch(config.mocUrl);require(response.ok,"MOC fetch failed");const bytes=await response.arrayBuffer();
    result.mocVersion=core.Version.csmGetMocVersion(bytes);result.latestMocVersion=core.Version.csmGetLatestMocVersion();
    result.consistency=core.Moc.prototype.hasMocConsistency(bytes)===1;
    require(result.consistency,"Official Core rejected MOC consistency; Model.fromMoc is deliberately NOT called");
    moc=core.Moc.fromArrayBuffer(bytes);require(moc,"Official Core Moc.fromArrayBuffer returned null");
    model=core.Model.fromMoc(moc);require(model,"Official Core Model.fromMoc returned null");result.coreCreated=true;
    require(model.drawables.count>0&&model.drawables.count<=4096&&model.parameters.count<=4096,"Model exceeds bounded probe size");
    neutral=Float32Array.from(model.parameters.defaultValues);reset();model.update();reference=snapshot();differences(reference);
    result.parameters=model.parameters.ids.map((id,i)=>({id,min:model.parameters.minimumValues[i],max:model.parameters.maximumValues[i],default:model.parameters.defaultValues[i]}));
    result.drawables=model.drawables.count;result.totalVertices=Array.from(model.drawables.vertexCounts).reduce((a,b)=>a+b,0);result.canvasInfo=model.canvasinfo;
    sweep();
    const images=await Promise.all(config.textures.map(src=>new Promise((resolve,reject)=>{const img=new Image();img.onload=()=>resolve(img);img.onerror=()=>reject(new Error("Texture failed: "+src));img.src=src;})));
    renderer=new OfficialRenderer(images);await renderer.initialize();update();require(readPixels().visiblePixels>10,"Core created a model but WebGL output is empty");
    for(let i=0;i<model.parameters.count;i++){
      const label=document.createElement("label");label.textContent=model.parameters.ids[i];const input=document.createElement("input");input.type="range";input.min=model.parameters.minimumValues[i];input.max=model.parameters.maximumValues[i];input.step="any";input.value=neutral[i];const output=document.createElement("output");output.textContent=String(neutral[i]);label.append(input,output);document.getElementById("parameters").append(label);inputs.set(model.parameters.ids[i],input);
      input.addEventListener("input",()=>{running=false;set(model.parameters.ids[i],Number(input.value));update();refreshControls();});
    }
    document.getElementById("play").onclick=()=>{running=!running;document.getElementById("play").textContent=running?"暂停":"播放";};
    document.getElementById("reset").onclick=()=>pose("neutral");document.getElementById("background").onclick=()=>canvas.classList.toggle("dark");
    window.probe={pose,play:value=>{running=Boolean(value);},resetMetrics:()=>{frames=0;intervals=[];phase=0;lastTime=undefined;samplingStart=performance.now();},report,beginRecording,finishRecording};
    result.loaded=true;status.textContent=config.name+" / MOC一致性通过 · 官方Core已创建模型";document.getElementById("summary").textContent=model.drawables.count+" meshes / "+model.parameters.count+" parameters · 原生Core计算 + 真实WebGL绘制，非PNG逐帧播放";
    window.chrome.webview.postMessage({type:"model-probe-ready",loaded:true});running=true;raf=requestAnimationFrame(loop);
  }
  start().catch(error=>{result.error=String(error?.stack||error);status.textContent=result.error;status.classList.add("error");window.probe={report:()=>result};window.chrome.webview.postMessage({type:"model-probe-ready",loaded:false,error:result.error});});
  window.addEventListener("beforeunload",()=>{
    running=false;if(raf!==undefined)cancelAnimationFrame(raf);
    for(const release of [()=>{if(video?.recorder.state==="recording")video.recorder.stop();},()=>video?.stream.getTracks().forEach(t=>t.stop()),()=>renderer?.release(),()=>model?.release(),()=>moc?._release()])try{release();}catch(error){runtimeError(error);}
    renderer=null;model=null;moc=null;video=null;
  });
})();
