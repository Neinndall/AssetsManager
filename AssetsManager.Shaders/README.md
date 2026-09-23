# AssetsManager.Shaders

`AssetsManager.Shaders` owns the shader-translation boundary used by the Viewer.

Responsibilities:
- Parse and reflect DXBC shader containers.
- Convert League DXBC shader bytecode to SPIR-V through the `vkd3d-shader` native distributed by the `ShadowDusk.HLSL` NuGet package.
- Normalize DXBC register semantics into stable shader interfaces.
- Patch SPIR-V/GLSL for the preview renderer contract.
- Cross-compile SPIR-V to GLSL through SPIRV-Cross.
- Produce shader sidecars describing uniform blocks, textures, samplers, attributes and compatibility patches.

The library is intentionally independent from MAP, VFX Studio, WAD resolution and OpenGL rendering. Consumers choose ShaderCache permutations and pass bytecode to this library; they remain responsible for binding the translated program to their renderer.

## Native dependency

There is no project-owned shader DLL or native source tree. `ShadowDusk.HLSL` supplies `libvkd3d-shader-1.dll` through NuGet runtime assets, and normal `dotnet build` copies that dependency transitively to the consuming `win-x64` application output.

The shader pipeline is documented and validated independently from the Viewer implementation.