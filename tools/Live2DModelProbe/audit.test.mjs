import assert from "node:assert/strict";
import test from "node:test";
await import("./Stage/audit.js");
const {bounds,eagle,poseSettings,stressAxes,stressCorners}=globalThis.ProbeAudit;
const ids=["ArtMeshFootwearL","ArtMeshFootwearR","ArtMeshEyelashL","ArtMeshEyelashR"];
const deltas=()=>ids.map(id=>({id,maximumVertexDelta:id.includes("Eyelash")?.02:0,maximumOpacityDelta:0,heightRatio:id.includes("Eyelash")?.1:1}));
const valid=()=>["ParamEyeLOpen","ParamEyeROpen","ParamBreath","ParamAngleX"].map(id=>({id,states:[{value:0,drawableDeltas:deltas()},{value:1,drawableDeltas:deltas()}]}));
test("bounds preserves coordinate frame",()=>assert.deepEqual(bounds([-1,2,3,-4,2,0]),{minX:-1,minY:-4,maxX:3,maxY:2,width:4,height:6}));
test("correct named eyes and rooted feet pass",()=>assert.equal(eagle(valid(),ids).passed,true));
test("missing foot cannot pass vacuously",()=>assert.equal(eagle(valid(),ids.slice(1)).passed,false));
test("missing eye parameter fails",()=>assert.equal(eagle(valid().slice(1),ids).passed,false));
test("any tiny foot drift fails exact root contract",()=>{const s=valid();s[2].states[1].drawableDeltas[0].maximumVertexDelta=1e-10;assert.equal(eagle(s,ids).passed,false);});
test("unclosed eye is not accepted just because geometry changed",()=>{const s=valid();s[0].states[0].drawableDeltas[2].heightRatio=.9;assert.equal(eagle(s,ids).passed,false);});
test("inert own eye fails even if another drawable changed",()=>{const s=valid();s[0].states[0].drawableDeltas[2].maximumVertexDelta=0;assert.equal(eagle(s,ids).passed,false);});
test("NaN eye geometry fails",()=>{const s=valid();s[0].states[0].drawableDeltas[2].heightRatio=NaN;assert.equal(eagle(s,ids).passed,false);});
test("empty parameter sweep fails",()=>assert.equal(eagle([],ids).passed,false));
test("neutral QA pose leaves default parameters intact",()=>assert.deepEqual(poseSettings("neutral"),[]));
test("gaze QA separates true eye motion from bounded head following",()=>{
  assert.deepEqual(poseSettings("gaze-eye-right"),[["ParamEyeBallX",1]]);
  assert.deepEqual(poseSettings("gaze-near-right"),[["ParamAngleX",9],["ParamEyeBallX",.35]]);
  assert.deepEqual(poseSettings("gaze-closed-up-right"),[["ParamEyeLOpen",0],["ParamEyeROpen",0],["ParamEyeBallX",1],["ParamEyeBallY",1]]);
});
test("eye intermediate QA poses use geometric quarter steps",()=>{
  for(const [name,value] of [["eyes-quarter",.25],["eyes-half",.5],["eyes-three-quarter",.75]])
    assert.deepEqual(poseSettings(name),[["ParamEyeLOpen",value],["ParamEyeROpen",value]]);
});

test("bird-beak QA inspects small openings as well as full open",()=>{
  for(const [name,value] of [["beak-start",.1],["beak-quarter",.25],["beak-half",.5],["beak-full",1]])
    assert.deepEqual(poseSettings(name),[["ParamBeakOpen",value]]);
});
test("combined head QA poses exercise all three angle limits",()=>{
  assert.deepEqual(poseSettings("head-up-left"),[["ParamAngleX","min"],["ParamAngleY","max"],["ParamAngleZ","max"]]);
  assert.deepEqual(poseSettings("head-down-right"),[["ParamAngleX","max"],["ParamAngleY","min"],["ParamAngleZ","min"]]);
});
test("unknown QA pose cannot silently become neutral",()=>{
  for(const name of ["missing","__proto__","constructor"])assert.throws(()=>poseSettings(name),/Unknown QA pose/);
});
test("QA pose definitions cannot be changed by a caller",()=>{
  const changed=poseSettings("head-up");changed[0][1]="min";
  assert.deepEqual(poseSettings("head-up"),[["ParamAngleY","max"]]);
});
test("stress path visits all eight XYZ corners and returns to start",()=>{
  const samples=Array.from({length:9},(_,i)=>stressAxes(i/8).values);
  assert.equal(new Set(samples.slice(0,8).map(v=>v.join(","))).size,8);
  assert.deepEqual(samples[0],[-1,1,1]);assert.deepEqual(samples[8],samples[0]);
});
test("stress interpolation stays bounded and continuous at keys",()=>{
  for(let i=0;i<=1000;i++)assert.ok(stressAxes(i/1000).values.every(v=>Number.isFinite(v)&&Math.abs(v)<=1));
  for(let i=1;i<8;i++){
    const a=stressAxes(i/8-1e-6).values,b=stressAxes(i/8+1e-6).values;
    assert.ok(a.every((v,j)=>Math.abs(v-b[j])<1e-8));
  }
});
test("stress path rejects invalid progress",()=>{for(const p of [NaN,Infinity,-.01,1.01])assert.throws(()=>stressAxes(p));});
test("stress corners hold exactly for the first 40 ms of each quarter-second",()=>{
  for(let i=0;i<8;i++){
    const expected=stressAxes(i/8).values;
    for(const seconds of [.001,.0167,.0334,.039])assert.deepEqual(stressAxes(i/8+seconds/2).values,expected);
    assert.notDeepEqual(stressAxes(i/8+.041/2).values,expected);
  }
});
test("stress coverage counts actual exact samples, never approximate corner values",()=>{
  const exact=Array.from({length:8},(_,i)=>({axes:stressAxes(i/8).values}));
  assert.equal(stressCorners(exact).length,8);
  assert.equal(stressCorners(exact.map(frame=>({axes:frame.axes.map(value=>value*.99)}))).length,0);
  assert.equal(stressCorners([...exact.slice(1),exact[1]]).length,7);
});
test("60 Hz samples offset from keys still visit every held corner",()=>{
  for(const offset of [0,.007,.013,.016]){
    const frames=[];
    for(let seconds=offset;seconds<2;seconds+=1/60)frames.push({axes:stressAxes(seconds/2).values});
    assert.equal(stressCorners(frames).length,8);
  }
});
