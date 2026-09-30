"use strict";
(() => {
  function bounds(positions) {
    let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;
    for(let i=0;i<positions.length;i+=2){minX=Math.min(minX,positions[i]);maxX=Math.max(maxX,positions[i]);minY=Math.min(minY,positions[i+1]);maxY=Math.max(maxY,positions[i+1]);}
    return {minX,minY,maxX,maxY,width:maxX-minX,height:maxY-minY};
  }
  const poses = {
    neutral: [],
    "eyes-closed": [["ParamEyeLOpen", "min"], ["ParamEyeROpen", "min"]],
    "eyes-quarter": [["ParamEyeLOpen", 0.25], ["ParamEyeROpen", 0.25]],
    "eyes-half": [["ParamEyeLOpen", 0.5], ["ParamEyeROpen", 0.5]],
    "eyes-three-quarter": [["ParamEyeLOpen", 0.75], ["ParamEyeROpen", 0.75]],
    "breath-high": [["ParamBreath", "max"]],
    "head-left": [["ParamAngleX", "min"]],
    "head-right": [["ParamAngleX", "max"]],
    "head-up": [["ParamAngleY", "max"]],
    "head-down": [["ParamAngleY", "min"]],
    "head-tilt-left": [["ParamAngleZ", "max"]],
    "head-tilt-right": [["ParamAngleZ", "min"]],
    "head-up-left": [["ParamAngleX", "min"], ["ParamAngleY", "max"], ["ParamAngleZ", "max"]],
    "head-down-right": [["ParamAngleX", "max"], ["ParamAngleY", "min"], ["ParamAngleZ", "min"]],
    "mouth-open": [["ParamMouthOpenY", "max"]],
    "double-lift": [["ParamWingLLift",0.8],["ParamWingRLift",0.8]],
    "beak-open": [["ParamBeakOpen",0.8]],
    "beak-start": [["ParamBeakOpen",0.1]],
    "beak-quarter": [["ParamBeakOpen",0.25]],
    "beak-half": [["ParamBeakOpen",0.5]],
    "beak-full": [["ParamBeakOpen",1]],
    "yawn-elbow": [["ParamWingRLift",0.88],["ParamWingRFold",0.4],["ParamBeakOpen",1]],
    "eat-elbow": [["ParamWingRLift",0.55],["ParamWingRFold",0.5]],
    "brows-sad": [["ParamBrowLY",1],["ParamBrowRY",1]],
    "brows-low": [["ParamBrowLY",-1],["ParamBrowRY",-1]],
    "hungry-face": [["ParamBrowLY",0.8],["ParamBrowRY",0.8],["ParamEyeLOpen",0.78],["ParamEyeROpen",0.78],["ParamAngleY",-8]],
    "busy-eyes": [["ParamBusyEyes",1]],
    "busy-eyes-half": [["ParamBusyEyes",1],["ParamEyeLOpen",0.5],["ParamEyeROpen",0.5]],
    "busy-eyes-closed": [["ParamBusyEyes",1],["ParamEyeLOpen",0],["ParamEyeROpen",0]],
    "gaze-eye-left": [["ParamEyeBallX",-1]],
    "gaze-eye-right": [["ParamEyeBallX",1]],
    "gaze-eye-up": [["ParamEyeBallY",1]],
    "gaze-eye-down": [["ParamEyeBallY",-1]],
    "gaze-near-left": [["ParamAngleX",-9],["ParamEyeBallX",-.35]],
    "gaze-near-right": [["ParamAngleX",9],["ParamEyeBallX",.35]],
    "gaze-near-up": [["ParamAngleY",5],["ParamEyeBallY",.3]],
    "gaze-near-down": [["ParamAngleY",-5],["ParamEyeBallY",-.3]],
    "gaze-closed-up-right": [["ParamEyeLOpen",0],["ParamEyeROpen",0],["ParamEyeBallX",1],["ParamEyeBallY",1]]
  };
  function poseSettings(name) {
    if (!Object.hasOwn(poses, name)) throw new Error("Unknown QA pose: " + name);
    return poses[name].map(([id, value]) => [id, value]);
  }
  function stressAxes(progress) {
    if (!Number.isFinite(progress) || progress < 0 || progress > 1) throw new Error("Stress progress must be in 0..1");
    const corners = [[-1,1,1],[1,-1,-1],[-1,-1,1],[1,1,-1],[-1,1,-1],[1,-1,1],[-1,-1,-1],[1,1,1],[-1,1,1]];
    const scaled=progress*8,index=Math.min(7,Math.floor(scaled));
    const t=Math.max(0,(scaled-index-0.16)/0.84),ease=t*t*(3-2*t);
    return {segment:index,values:corners[index].map((value,axis)=>value+(corners[index+1][axis]-value)*ease)};
  }
  function stressCorners(frames) {
    return [...new Set(frames.filter(frame=>frame.axes.every(value=>value===1||value===-1))
      .map(frame=>frame.axes.join(",")))].sort();
  }
  function eagle(sweeps, ids) {
    const feet=["ArtMeshFootwearL","ArtMeshFootwearR"], eyePairs=[["ParamEyeLOpen","ArtMeshEyelashL"],["ParamEyeROpen","ArtMeshEyelashR"]];
    const missing=feet.concat(eyePairs.map(p=>p[1])).filter(id=>!ids.includes(id));
    const footStates=sweeps.flatMap(p=>p.states.flatMap(s=>s.drawableDeltas.filter(d=>feet.includes(d.id)).map(d=>({parameter:p.id,value:s.value,...d}))));
    const maximumFootDelta=Math.max(0,...footStates.map(s=>s.maximumVertexDelta));
    const eyes=eyePairs.map(([parameter,drawable])=>{
      const sweep=sweeps.find(p=>p.id===parameter), closed=sweep?.states.find(s=>s.value===0)?.drawableDeltas.find(d=>d.id===drawable);
      return {parameter,drawable,closedHeightRatio:closed?.heightRatio??null,maximumVertexDelta:closed?.maximumVertexDelta??0,passed:!!closed&&closed.maximumVertexDelta>1e-7&&closed.heightRatio<=0.35};
    });
    return {passed:missing.length===0&&maximumFootDelta===0&&eyes.every(e=>e.passed),missing,maximumFootDelta,feetExactForAllSweeps:maximumFootDelta===0,eyes,footStates,
      note:"Geometry contract only; eye white highlights, silhouette and smoothness still require visual review"};
  }
  globalThis.ProbeAudit={bounds,eagle,poseSettings,stressAxes,stressCorners};
})();
