<div align="center">
  <img src="AssetsManager/Resources/Img/logo.ico" alt="AssetsManager logo" width="100">
</div>

# 🛠️ AssetsManager

[![Latest Release](https://img.shields.io/github/v/release/Neinndall/AssetsManager?color=yellow&logo=github&logoColor=white&label=Release&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![Downloads](https://img.shields.io/github/downloads/Neinndall/AssetsManager/total?color=blue&logo=github&logoColor=white&label=Downloads&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![License](https://img.shields.io/github/license/Neinndall/AssetsManager)](LICENSE)

AssetsManager is a powerful tool designed for League of Legends enthusiasts who need to analyze, manage, and track changes to game assets from PBE updates. It offers a comprehensive suite of features for deep asset inspection, 3D model visualization, archive exploration, and real-time monitoring

## 🚀 Getting Started

### Requirements

- Windows x64.
- .NET 10 Desktop Runtime for framework-dependent builds.
- Microsoft Edge WebView2 Runtime for embedded web and media content.
- A local League of Legends installation or extracted assets for the workflows that use game files.

### Installation

1. Download a build from the [Releases page](https://github.com/Neinndall/AssetsManager/releases).
2. Extract the complete archive and launch `AssetsManager.exe` from the extracted folder.
3. Configure your LIVE/PBE installation paths and output folders in **Settings**. The Home dashboard shows whether configured paths are ready, missing, or require setup.

The integrated Update Manager provides application updates. Settings are persisted in `config.json`, including update channels, background checks, export preferences, and notifications.

## 🏛️ Core Views

### 🏠 Home

The starting point for checking your setup and opening the application's main tools.

- **Path Readiness:** Check whether configured game installations and output folders are ready, missing, or require setup.
- **Quick Tools:** Open the batch converter, Audio Player, and Quick Notepad.
- **Workspace Access:** Navigate to asset comparison, exploration, model previews, and monitoring.

### 🔄 Comparator

Compare game installations or versions and inspect what changed inside their WAD archives.

- **WAD Comparison:** Use archive checksums to identify added, modified, removed, and renamed assets, including changes involving entire WAD containers.
- **Diagnostic Overview:** Review asset distribution and payload analysis, then inspect results through hierarchy views and visual asset previews.
- **Change Details:** Inspect old and new paths, file sizes, and change categories.
- **Asset Differences:** Compare supported text, textures, audio, and models side by side; inspect semantic BIN differences in Ritobin format.
- **Export Results:** Extract original assets or save converted content, with per-file status and direct access to output folders.
- **Comparison History:** Reopen recorded comparisons and inspect archived assets from associated backups.

### 📂 Explorer

Browse game archives and extracted files with a shared navigation, preview, and export interface.

- **Source Modes:** Explore **LIVE**, **PBE**, **LOCAL**, and comparison **RESULTS**.
- **Navigation Toolbar:** Switch browsing modes, use tree or grid views, follow breadcrumbs, and configure grouping.
- **Image Gallery:** Browse textures with asynchronously generated thumbnails and asset metadata.
- **Search and Go To:** Filter assets, highlight matches, and navigate directly to logical paths.
- **Asset Previews:** Inspect textures, audio, models, and formatted data, including side-by-side comparisons for supported content.
- **Image Merger:** Combine selected textures into contact sheets.
- **Favorites and Pinned Tabs:** Keep frequently used assets accessible across sessions and pin previews while browsing.
- **Asset Monitoring:** Add files or containers to monitoring directly from the Explorer.
- **Context Actions:** Extract original files or save supported formats using your export preferences, while preserving their folder hierarchy.

Supported content includes `.dds`/`.tex` textures, `.skn` models, `.anm` animations, Wwise `.wem`/`.bnk`/`.wpk` audio, `.bin`/`.troybin`, string tables, compiled Lua, and common text and web formats. Encrypted esports textures require the corresponding key.

**Audio Bank Inspection** exposes event and container hierarchies, resolves linked banks, and supports playback and extraction of associated media.

### 🎮 Viewer

Inspect character models, animations, chromas, maps, and particles through the standard Viewer and VFX Studio workflows.

#### Model Viewer

- **Load Project:** Load character meshes and their dependencies, inspect submeshes, and play `.anm` animations.
- **Chroma Library:** Browse skin and chroma variants and compare their models and textures.
- **Model Arrangement:** Arrange multiple models, select them with **Ctrl+click** or **Shift+click**, and move selections with the XYZ gizmo.
- **Viewport Display:** Configure the ground, grid, sky, and antialiasing.
- **Visual Export:** Capture UHD PNG snapshots with transparency support.

#### VFX Studio

Load an extracted project folder to inspect BIN data, character assets, particle systems, and `.mapgeo` maps with their dependencies.

- **Project Tree:** Browse map geometry, materials, chunks, characters, and particles.
- **Character Preview:** Play animations and their attached particle effects, use available spell previews, and inspect supported character states.
- **Scene Actors:** Add characters to the scene, select multiple actors with **Ctrl+click** or **Shift+click**, and move them together with the gizmo.
- **Emitter Inspector:** Inspect particle systems and emitters, filter timeline rows, and control visibility and solo states.
- **Timeline:** Play, pause, seek, adjust playback speed, and loop previews. **Loop is enabled by default.**
- **T-Pose:** Toggle the character's bind pose from the viewport toolbar.
- **Map Environment:** Preview supported map variants, elemental rifts, structures, lighting, and map effects.
- **VFX and Shaders:** Enable **Map VFX Effects** and **Shaders** when needed; both are disabled by default. Game shader previews use a dedicated DXBC → SPIR-V → GLSL translation pipeline.

#### Viewport Navigation

| Control | Action |
| --- | --- |
| Left mouse drag | Look around from the camera position in perspective mode |
| Alt + left mouse drag | Orbit around the camera target |
| W / A / S / D | Move through the scene |
| Shift while moving | Move faster |
| Ctrl while moving | Move slower |
| Mouse wheel | Zoom |

Camera presets and framing controls are available in the viewport. Selecting the already loaded map root in the VFX Studio project tree preserves the camera position.

### 🔬 Hash Lab

Research unresolved asset paths and identifiers using game content and persistent local inventories.

- **Research Domains:** Inspect unresolved GAME, LCU, and BIN hashes.
- **WAD and BIN Search:** Search archive content and BIN properties for candidate paths and identifiers.
- **Candidate Validation:** Check discoveries against asset hashes before accepting matches.
- **Discovery Tracking:** Preserve unresolved hashes and discoveries across patches.

### 📰 News

Browse Riot articles, patch notes, and videos inside the application.

- **Categories and Search:** Filter news by category and search article or video content.
- **Inline Reader:** Read supported articles with images and embedded media.
- **Notifications:** Receive alerts when new content is found.

### 📡 Monitor

Track game updates and asset changes, manage historical data, and access version and API tools.

- **Dashboard:** Check background service status, PBE availability, and update readiness.
- **Asset Watcher:** Monitor local game files and plugins, with change history and comparisons.
- **Asset Tracker:** Follow selected assets on Riot's CDN.
- **History:** Reopen past comparisons and monitoring records.
- **Backups:** Manage local installation snapshots and their asset dependencies.
- **Manage Versions:** Discover regional versions, inspect RMAN manifests, and download client or game data.
- **API Center:** Inspect supported Riot API content, including sales, Mythic Shop, and pass rewards, with PNG export.

## 🧰 Utilities

- Batch image and audio conversion.
- Audio Player with playlists, saved packs, and YouTube playback.
- Quick Notepad with syntax highlighting.

## 🤝 Contributing

Open an issue using the [issue templates](.github/ISSUE_TEMPLATE), or submit a pull request with a description of the change and relevant validation. Consult the [changelog](AssetsManager/changelogs.txt) for recent changes.

## 📄 License

AssetsManager is licensed under the [GNU General Public License v3.0](LICENSE). Bundled dependencies retain their respective licenses.
