"use strict";
(() => {
  function bounds(positions) {
    let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;
    for(let i=0;i<positions.length;i+=2){minX=Math.min(minX,positions[i]);maxX=Math.max(maxX,positions[i]);minY=Math.min(minY,positions[i+1]);maxY=Math.max(maxY,positions[i+1]);}
    return {minX,minY,maxX,maxY,width:maxX-minX,height:maxY-minY};
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
  globalThis.ProbeAudit={bounds,eagle};
})();
