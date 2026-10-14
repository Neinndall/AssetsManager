# AssetsManager.Shaders

`AssetsManager.Shaders` owns the shader-translation boundary used by the Viewer.

Responsibilities:
- Parse and reflect DXBC shader containers.
- Convert League DXBC shader bytecode to SPIR-V through the `dxbc-spirv` native (`dxbc_spv_lol.dll`).
- Normalize DXBC register semantics into stable shader interfaces.
- Patch SPIR-V/GLSL for the preview renderer contract.
- Cross-compile SPIR-V to GLSL through SPIRV-Cross.
- Produce shader sidecars describing uniform blocks, textures, samplers, attributes and compatibility patches.

The library is intentionally independent from MAP, VFX Studio, WAD resolution and OpenGL rendering. Consumers choose ShaderCache permutations and pass bytecode to this library; they remain responsible for binding the translated program to their renderer.

## Native dependency

There is no NuGet-owned shader compiler. `runtimes/win-x64/native/dxbc_spv_lol.dll` is the project-owned SM5 shim built from the pinned `dxbc-spirv` + `SPIRV-Headers` revisions under `native/dxbc-spv/` (see its README), and normal `dotnet build` copies it via `Content/PreserveNewest` to the consuming `win-x64` application output. SPIRV-Cross arrives via the `Vortice.SpirvCross` NuGet package.

The shader pipeline is documented and validated independently from the Viewer implementation.