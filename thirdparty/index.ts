import { $, Glob } from "bun";
import { join } from "node:path";

const outDir = join(__dirname, "ksa");

// c:\Program Files\Kitten Space Agency\Content\Core\

const DLLS = [
  "Brutal.Concurrency.dll",
  "Brutal.Core.Collections.dll",
  "Brutal.Core.Common.dll",
  "Brutal.Core.Logging.dll",
  "Brutal.Core.Maths.dll",
  "Brutal.Core.Memory.dll",
  "Brutal.Core.Numerics.dll",
  "Brutal.Core.Package.dll",
  "Brutal.Core.Strings.dll",
  "Brutal.Fmod.dll",
  "Brutal.Glfw.dll",
  "Brutal.Gli.dll",
  // "Brutal.Gli.Texture.dll",
  "Brutal.Gltf.dll",
  "Brutal.ImGui.Abstractions.dll",
  "Brutal.ImGui.dll",
  "Brutal.ImGui.Extensions.dll",
  "Brutal.ImPlot.dll",
  "Brutal.Ktx.dll",
  // "Brutal.Ktx.Texture.dll",
  "Brutal.Monitor.Common.dll",
  "Brutal.Monitor.Host.dll",
  "Brutal.RakNet.dll",
  "Brutal.Render.Common.dll",
  "Brutal.Render.Mesh.dll",
  "Brutal.ShaderCompiler.dll",
  "Brutal.Stb.dll",
  // "Brutal.Stb.Texture.dll",
  "Brutal.Texture.Abstractions.dll",
  "Brutal.Texture.dll",
  "Brutal.Vulkan.Abstractions.dll",
  "Brutal.Vulkan.dll",
  "KSA.dll",
  "Planet.Core.dll",
  "Planet.Render.Core.dll",
];


const CORE_ASSETS_FOLDERS = [
  "Animations",
  "Characters",
  "defaultvehicles",
  "MeshCollections",
  "Meshes",
  "Shaders",
  "Textures",
];

const CORE_ASSETS_DIR = join("C:", "Program Files", "Kitten Space Agency", "Content", "Core");

const xmlGlob = new Glob("*.xml");
const CORE_ASSETS_FILES: string[] = [];
for (const name of xmlGlob.scanSync({ cwd: CORE_ASSETS_DIR, onlyFiles: true })) {
  const content = await Bun.file(join(CORE_ASSETS_DIR, name)).text();
  if (content.includes("<Assets>")) {
    CORE_ASSETS_FILES.push(name);
  }
}

console.log(`Found ${CORE_ASSETS_FILES.length} asset XML files:`, CORE_ASSETS_FILES);


for (const folder of CORE_ASSETS_FOLDERS) {
  console.log(`Copying ${folder}...`);
  await $`mkdir -p ${join(outDir, "Content", "Core")}`;
  const folderPath = join("C:", "Program Files", "Kitten Space Agency", "Content", "Core", folder);
  await $`cp -R ${folderPath} ${join(outDir, "Content", "Core", folder)}`;
}

for (const asset of CORE_ASSETS_FILES) {
  console.log(`Copying ${asset}...`);
  const assetPath = join(CORE_ASSETS_DIR, asset);
  await $`cp ${assetPath} ${join(outDir, "Content", "Core", asset)}`;
}

for (const dll of DLLS) {
  console.log(`Decompiling ${dll}...`);
  const dllPath = join("C:", "Program Files", "Kitten Space Agency", dll);
  await $`dotnet tool run ilspycmd -o ${outDir} -p -r 'C:\Program Files\Kitten Space Agency' ${dllPath}`;
}
