# SMAA reference assets

Shaders and lookup tables are adapted from Three.js r185, the dependency used by the audited reference viewport.

- Shader source: https://github.com/mrdoob/three.js/blob/r185/examples/jsm/shaders/SMAAShader.js
- Pass and embedded lookup PNGs: https://github.com/mrdoob/three.js/blob/r185/examples/jsm/postprocessing/SMAAPass.js
- License: LICENSE.txt (Three.js MIT license).
- Preset: SMAA 1x Medium, color edge detection, threshold 0.1, 8 search steps.
- Native adaptation: fullscreen triangle, GLSL 330/300 ES syntax, RGBA16F intermediate targets.
- Lookup PNG bytes are unchanged; raw RGBA upload preserves their row order.
- Area filtering is linear; search filtering is nearest. Both clamp and have no mipmaps.

- Area.png: 160 x 560, SHA256 b5a8e3bb3fbf11136d1411c6b7454f4469358559352987a074e22047582c0625.
- Search.png: 66 x 33, SHA256 025eb54fb8c4c2858a7d6d9743b88919fe5f9c661329c447a8f1e28971ae7403.
