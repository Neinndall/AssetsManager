<div align="center">
  <img src="https://github.com/Neinndall/AssetsManager/blob/main/AssetsManager/Resources/Img/logo.ico" alt="Logo" width="100">
</div>

## 🛠️ AssetsManager

[![Latest Release](https://img.shields.io/github/v/release/Neinndall/AssetsManager?color=yellow&logo=github&logoColor=white&label=Release&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![Downloads](https://img.shields.io/github/downloads/Neinndall/AssetsManager/total?color=blue&logo=github&logoColor=white&label=Downloads&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![License](https://img.shields.io/github/license/Neinndall/AssetsManager)](https://github.com/Neinndall/AssetsManager/blob/main/LICENSE)

AssetsManager is a powerful tool designed for League of Legends enthusiasts who need to analyze, manage, and track changes to game assets from PBE updates. It offers a comprehensive suite of features for deep asset inspection, 3D model and VFX visualization, hash discovery and research, archive exploration, and real-time monitoring.

## 🏛️ Core Systems

AssetsManager is built upon several primary technical pillars, designed to provide a professional-grade workspace for League of Legends asset analysis:

*   **Comparator**: A specialized differential analysis engine.
    *   **Fast Mode**: Uses reflection-based XXHash64 checksums to identify modified, renamed, or removed files across WAD versions in seconds.
    *   **Diagnostic Studio**: A dedicated workspace for exploring results via **Hierarchy Tree**, visual **Asset Discovery** gallery, and technical **Patch Intelligence** analytics.
    *   **Results Sessions**: The comparison output now includes a full **Results tab** with per-asset export status (EXPORTED / FAILED / QUEUED), batch **Extract** and **Save** actions, and direct access to the output folders of any exported asset.
    *   **Semantic BIN Diffing**: BIN differences now show only semantic object and property changes with resolved paths, rendered through the canonical Ritobin structure.
    *   **Metadata Traceability**: High-density inspector providing size deltas, rename history, and category-based impact mapping.
*   **Explorer**: A high-performance archive navigation suite with specialized modules:
    *   **Universal Mode Support**: A single, high-performance engine for browsing **LIVE**, **PBE**, **LOCAL** extractions, and **RESULTS** comparison data.
    *   **Adaptive Engineering Toolbar**: A dual-panel architecture featuring a clean header for core actions and an expanded technical suite (Mode, Grid, Breadcrumb, Grouping).
    *   **Premium Gallery (Grid View)**: High-performance visual explorer with asynchronous thumbnail generation, caching, filter resilience and range selection (SHIFT / CTRL+SHIFT).
    *   **Intelligent Search & Navigation**: Deep-seek engine with match highlighting and asynchronous "Go To" absolute path navigation.
    *   **Image Merger**: Specialized tray to composite multiple textures into professional contact sheets.
    *   **Quick Access Favorites**: Persistent system to pin frequently used assets and watch specific containers for instant cross-session navigation.
    *   **Direct Asset Monitoring**: Seamless integration with the Monitoring Engine to track specific files or containers directly from the explorer.
    *   **Contextual Traceability**: Professional right-click menu for monitoring, extracting, saving, and collecting into the Image Merger.
*   **Model Viewer**: A high-fidelity 3D studio for asset and VFX inspection.
    *   **Advanced Rendering**: Native support for `.skn` meshes and complex `.mapgeo` environments with PBR-lite material resolution.
    *   **VFX Studio**: BIN-driven particle playback with timeline controls (play, pause, stop, speed), emitter layers deck matching Riot's editor layout, per-emitter visibility toggles, automatic dependency resolution, and CPU skinning for mesh emitters.
    *   **Mesh Studio**: Direct selection and movement of loaded models in the viewport via transform gizmo (XYZ), auto-arrangement of selected models, and user-controlled camerawork.
    *   **Animation Playback**: Features Linear Blend Skinning (LBS) calculated via Parallel.For for smooth playback of `.anm` sequences.
    *   **Chroma Library**: Family-based grouping of skins with variant selection, mesh-visibility synchronization across models, and direct scene inspection.
    *   **Visual Export**: Capture professional **4K snapshots** of skins, models, and VFX with transparency support.
*   **Monitor**: An automated telemetry and tracking center.
    *   **Bento Dashboard**: A modular command center with real-time telemetry, PBE server status, system health, and update readiness.
    *   **Asset Watcher**: Background tracking of local game files with automatic diff logging and version history preservation.
    *   **Asset Tracker**: Persistent monitoring of high-priority Riot CDN assets with intelligent sequence management and extension fallback.
    *   **History, Backups & Versions**: Centralized registries for past comparison results, installation snapshots with MAIN/BACKUP role identification, and RMAN version manifests.

## 🔬 Hash Intelligence Lab

A specialized engine for discovering and resolving unknown playing-game hashes with full persistence:

*   **GAME / LCU / BIN / RST Domains**: Independent inventories and guessing suites per domain with typed local catalogs and persistent research tracking.
*   **WAD Path GREP**: Extracts candidate paths directly from live and archived WADs with XXH64 checks.
*   **BIN Local GREP**: Scans PROP/PTCH binary trees in local `.bin` chunks, extracting **ObjectLink** values and typed entry hashes, and classifies them as BIN Entry candidates against the local catalogs.
*   **Unified Unknowns Tracking**: Centralized and persistent tracking of unknown hashes with per-patch historical separation and inline verification.
*   **Automated Promotion**: Successfully guessed hashes are promoted directly to the main binary hash catalogs.

## 📰 Riot News

Stay up to date with the official Riot ecosystem:

*   **Categorized Feeds**: Separate feeds for Dev, Esports, Game Updates, Lore, Media, and Patch Notes, each limited to the most recent articles.
*   **Full Article Reader**: Inline rich rendering with banners, authors, and videos, plus search and content-type filtering (Articles / Videos).
*   **Background Discovery**: Optionally controlled notification integration that reports newly published articles through the system notification hub with click-through.

## 🔍 Explorer Capabilities

The **Archive Explorer** includes advanced technical features for deep game data analysis:

*   **Multi-Format Visualization**: High-fidelity previewers for a wide range of game formats:
    *   **3D Models**: Native rendering of `.skn` meshes and `.mapgeo` environments.
    *   **Textures**: Instant preview of `.dds` and `.tex` files with transparency support, plus TGA decoding and diffing. Encrypted Riot esports textures are identified explicitly and require the corresponding decryption key before they can be decoded.
    *   **Audio**: Real-time playback of audio files and Wwise banks (`.wem`, `.bnk`, `.wpk`) with event-to-media mapping.
    *   **Code & Data**: Decompilation of `.luabin64` (Lua 5.1) and formatting of `.bin`, `.troybin`, `.stringtable`, `.preload`, `.json`, `.xml`, `.svg`, `.css`, `.js`, and `.html` with syntax highlighting.
*   **Adaptive Toolbar**: Dual-panel architecture with a clean header and an expanded technical suite (Mode, Grid, Breadcrumb).
*   **Intelligent Navigation**: Deep-seek engine with match highlighting and asynchronous "Go To" absolute path navigation.
*   **Contextual Tools**: Image Merger for texture sheets, Favorites for quick access, WATCH tracking, and a professional transport bar for extraction and filename.

## 🔊 Audio Intelligence

The **Audio Bank Center** provides professional-grade tools for inspecting and extracting Wwise-based game sound:

*   **HIRC Hierarchy Traversal**: Deep parsing of bank structures and event-hierarchy restoration, with support for Random, Switch, and Blend Containers.
*   **Linked Master Bank Discovery**: Intelligent engine that automatically identifies and links regional VO containers with their corresponding master metadata for full logic reconstruction.
*   **Dynamic Decoding**: Real-time playback and extraction of `.wem` assets using an integrated high-performance decoding motor.
*   **Event-to-Media Mapping**: Instant identification of which audio files are triggered by specific game events, including event-based subcontainers.
*   **Smart Linking Engine**: Automatic recognition of local regions and VO containers to reconstruct complete legacy sound logic.

## 🏠 Home Dashboard

The **Home Dashboard** acts as the professional launcher and central hub of the application:

*   **Unified Entry Point**: Instant one-click access to all Core Systems and secondary utilities from a single, high-fidelity HUD interface.
*   **Environment Awareness**: Dynamic status badges (READY, SETUP, MISSING) for LIVE, PBE, and LOCAL paths, ensuring your workspace is always properly configured.
*   **Greeting & Context**: Personalized greeting system and quick-start subtitles to guide your workflow.
*   **Quick Utility Access**: Icon-based links to the **Asset Converter**, **Audio Player**, and **Quick Notepad**.

## 🧰 Secondary Utilities

*   **Asset Converter**: Unified multi-threaded engine for batch processing images (`.dds`, `.tex`) and audio (`.wem`, `.ogg`, `.mp3`).
*   **Audio Player**: Advanced session-based player featuring playlist packs and YouTube streaming integration.
*   **Quick Notepad**: Integrated technical editor powered by AvalonEdit for quick note-taking during analysis.

## 📡 Monitoring Engine

AssetsManager includes a robust monitoring suite designed as a technical command hub for tracking game updates and asset integrity:

*   **Dashboard**: A global integrity hub providing real-time telemetry for background services, PBE server status, and a consolidated overview of system health and update readiness.
*   **Asset Watcher**: Powered by a hybrid integrity engine (XXHash64), it performs automated monitoring of local game files and plugins with version-history and granular diff reporting.
*   **Asset Tracker**: Enables persistent monitoring of high-priority assets over the Riot CDN, with sequence management, extension fallback, and multi-format candidate scanning.
*   **Backups**: Comprehensive management of local game snapshots (LIVE/PBE), with role identification (MAIN vs BACKUP), integrity manifests, sorting, and direct folder access.
*   **History**: A persistent registry for all past WAD comparisons with asynchronous pagination and full reconstruction from cached JSON indices.
*   **API Center**: Advanced utility for querying Riot's official production APIs (Sales, Mythic Shop, Pass Rewards), with FAST pass / Mission discovery and unified professional PNG export.
*   **Manage Versions**: Specialized view for regional version discovery and RMAN manifest acquisition, with direct client and game-data deployment.

## 🚀 Getting Started

### Prerequisites
*   [.NET 10.0 Runtime](https://dotnet.microsoft.com/es-es/download/dotnet/thank-you/runtime-desktop-10.0.2-windows-x64-installer) (Desktop) installed.

### Installation & Updates
1.  **Download**: Get the latest `AssetsManager_vX.X.X.X.zip` from the [Releases page](https://github.com/Neinndall/AssetsManager/releases).
2.  **Extract & Run**: Unzip to any folder and launch `AssetsManager.exe`.
3.  **Updates**: The integrated **Update Manager** will notify you of new versions for seamless clean or preserved installations.

## ⚙️ Configuration
All settings are managed via the **Settings** window and persisted in `config.json`, enabling deep customization of preferred clients, monitoring frequencies, export formats, and extraction preferences. Detailed technical options (update channels, background checks, notification preferences) are available under the **Advanced** section.

## 🤝 Contributing
Contributions are welcome! Feel free to fork the repo, submit **pull requests**, or open **issues** to discuss technical improvements or report bugs. Check our [issue templates](.github/ISSUE_TEMPLATE) for guided feedback.

## 📄 License
This project is licensed under the [GNU General Public License v3.0](LICENSE).