import { build } from "esbuild";
import { readFile, mkdir, writeFile, access } from "node:fs/promises";
import { resolve, isAbsolute, join } from "node:path";
import { createHash } from "node:crypto";
const [sdkArg, outputArg] = process.argv.slice(2);
if (!sdkArg || !outputArg || !isAbsolute(sdkArg) || !isAbsolute(outputArg)) throw new Error("Usage: node build-framework.mjs <absolute SDK root> <absolute output bundle.js>");
const sdk = resolve(sdkArg), output = resolve(outputArg);
if(output.toLowerCase()===sdk.toLowerCase()||output.toLowerCase().startsWith(sdk.toLowerCase()+"\\")||output.toLowerCase().startsWith(sdk.toLowerCase()+"/"))throw new Error("Output cannot be inside SDK inputs");
for(const path of [output,output+".provenance.json"]){let exists=false;try{await access(path);exists=true;}catch(error){if(error.code!=="ENOENT")throw error;}if(exists)throw new Error("Refusing to overwrite existing bundle/provenance: "+path);}
const info = await readFile(join(sdk, "cubism-info.yml"), "utf8");
if (!info.includes("version: 5-r.5")) throw new Error("Official Web SDK 5-r.5 required");
const modulePath = p => JSON.stringify(join(sdk, "Framework/src", p).replaceAll("\\", "/"));
const source = `
import { CubismFramework } from ${modulePath("live2dcubismframework.ts")};
import { CubismModel } from ${modulePath("model/cubismmodel.ts")};
import { CubismRenderer_WebGL } from ${modulePath("rendering/cubismrenderer_webgl.ts")};
import { CubismShaderManager_WebGL } from ${modulePath("rendering/cubismshader_webgl.ts")};
import { CubismMatrix44 } from ${modulePath("math/cubismmatrix44.ts")};
window.ProbeFramework={CubismFramework,CubismModel,CubismRenderer_WebGL,CubismShaderManager_WebGL,CubismMatrix44};`;
await mkdir(resolve(output, ".."), { recursive: true });
console.log("EXACT OUTPUT " + output);
const result = await build({ stdin: { contents: source, resolveDir: sdk, loader: "ts" }, outfile: output, bundle: true, format: "iife", platform: "browser", target: "es2022", metafile: true, legalComments: "inline", write:false });
const inputs = [];
for (const file of Object.keys(result.metafile.inputs).filter(p => p !== "<stdin>")) {
  const bytes = await readFile(resolve(file));
  inputs.push({ name: resolve(file).slice(sdk.length + 1).replaceAll("\\", "/"), sha256: createHash("sha256").update(bytes).digest("hex") });
}
const bundle = result.outputFiles[0].contents;
await writeFile(output,bundle,{flag:"wx"});
await writeFile(output + ".provenance.json", JSON.stringify({ sdkRelease: "5-r.5", sdkInfo: info, esbuild: "0.25.10", bundleSha256: createHash("sha256").update(bundle).digest("hex"), inputs, notice: "Locally built official Framework only; NOT for public redistribution; Core is not embedded" }, null, 2),{flag:"wx"});
