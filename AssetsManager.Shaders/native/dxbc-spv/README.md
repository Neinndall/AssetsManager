# dxbc-spirv native backend

AssetsManager.Shaders translates League DXBC programs through the same `dxbc-spirv` SM5 lowering used by the reference Hexshade pipeline before the managed SPIR-V/GLSL patching stage.

Pinned inputs:

- dxbc-spirv: `bf14419e5fa7eacb817b7b632f03cb61d61bbad7`
- SPIRV-Headers: `c8ad050fcb29e42a2f57d9f59e97488f465c436d`
- Zig bootstrap compiler used for the checked-in Windows x64 binary: `0.15.2`

`shim.cpp` mirrors the reference compiler options: SM5 enabled, debug names, no 16-bit lowering, unstructured constant buffers, typed raw/structured buffers, descriptor-load relocation/optimization, sample-count lowering, and SPIR-V debug names. The only Windows-specific change is exporting the two C ABI functions so .NET can load them from `dxbc_spv_lol.dll`.

Run `build.ps1` to rebuild `../../runtimes/win-x64/native/dxbc_spv_lol.dll`. The script downloads only the pinned source archives and a temporary Zig toolchain; none of those build inputs are copied into the repository.

The native runtime is distributed under the upstream licenses included beside this file.
