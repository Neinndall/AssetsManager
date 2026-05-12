<div align="center">
  <img src="https://github.com/Neinndall/AssetsManager/blob/main/AssetsManager/Resources/Img/logo.ico" alt="Logo" width="100">
</div>

## 🛠️ AssetsManager

[![Latest Release](https://img.shields.io/github/v/release/Neinndall/AssetsManager?color=yellow&logo=github&logoColor=white&label=Release&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![Downloads](https://img.shields.io/github/downloads/Neinndall/AssetsManager/total?color=blue&logo=github&logoColor=white&label=Downloads&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![License](https://img.shields.io/github/license/Neinndall/AssetsManager)](https://github.com/Neinndall/AssetsManager/blob/main/LICENSE)

AssetsManager is a powerful tool designed for League of Legends enthusiasts who need to analyze, manage, and track changes to game assets from PBE updates. It offers a comprehensive suite of features for deep asset inspection, 3D model visualization, archive exploration, and real-time monitoring.

## 🏛️ Core Systems

AssetsManager is built upon four primary technical pillars, designed to provide a professional-grade workspace for League of Legends asset analysis:

*   **Comparator**: A specialized differential analysis engine.
    *   **Fast Mode**: Uses reflection-based XXHash64 checksums to identify modified or renamed files across WAD versions in seconds.
    *   **Diagnostic Studio**: A dedicated workspace for exploring results via **Hierarchy Tree**, visual **Asset Discovery** gallery, and technical **Patch Intelligence** analytics.
    *   **Metadata Traceability**: High-density inspector providing size deltas, rename history, and category-based impact mapping.
*   **Explorer**: A high-performance archive navigation suite with specialized modules:
    *   **Universal Mode Support**: A single, high-performance engine for browsing **LIVE**, **PBE**, **LOCAL** extractions, and **RESULTS** comparison data.
    *   **Adaptive Engineering Toolbar**: A dual-panel architecture featuring a clean header for core actions and an expanded technical suite (Mode, Grid, Breadcrumb, Grouping).
    *   **Premium Gallery (Grid View)**: High-performance visual explorer with asynchronous thumbnail generation and rich metadata badges.
    *   **Intelligent Search & Navigation**: Deep-seek engine with match highlighting and asynchronous "Go To" absolute path navigation.
    *   **Image Merger**: Specialized tray to composite multiple textures into professional contact sheets.
    *   **Quick Access Favorites**: Persistent system to pin frequently used assets for instant cross-session navigation.
    *   **Direct Asset Monitoring**: Seamless integration with the Monitoring Engine to track specific files or containers directly from the explorer.
    *   **Contextual Traceability**: Professional right-click menu for extracting, saving, and pinning to tabs.
*   **Model Viewer**: A high-fidelity 3D studio for asset inspection.
    *   **Advanced Rendering**: Native support for `.skn` meshes and complex `.mapgeo` environments with PBR-lite material resolution.
    *   **Animation Playback**: Features Linear Blend Skinning (LBS) calculated via Parallel.For for smooth playback of `.anm` sequences.
    *   **Visual Export**: Capture professional 4K snapshots of skins and models with transparency support.
*   **Monitor**: An automated telemetry and tracking center.
    *   **Real-time Intelligence**: Continuous monitoring of PBE server status and regional version discovery via Riot APIs.
    *   **Asset Watcher**: Background tracking of local game files with automatic diff logging and version history preservation.
    *   **History & Tracker**: Centralized registries for persistent asset monitoring and instant access to past comparison results.

## 🏠 Home Dashboard

The **Home Dashboard** acts as the professional launcher and central hub of the application:

*   **Unified Entry Point**: Instant access to all Core Systems and secondary utilities from a single, high-fidelity HUD interface.
*   **Environment Awareness**: Features dynamic status badges (READY, SETUP, MISSING) for LIVE, PBE, and LOCAL paths, ensuring your workspace is always properly configured.
*   **Greeting & Context**: Personalized greeting system and quick-start subtitles to guide your workflow.
*   **Quick Utility Access**: Discrete icon-based links to support apps like the **Asset Converter**, **Audio Player**, and **Quick Notepad**.

### 🔍 Specialized Archive Explorer Functions
*   **Multi-Format Visualization**: High-fidelity previewers for a wide range of game formats:
    *   **3D Models**: Native rendering of `.skn`, `.sco`, `.scb` meshes and `.mapgeo` environments.
    *   **Textures**: Instant preview of `.dds` and `.tex` files with transparency support.
    *   **Audio**: Real-time playback of `.wem`, `.bnk`, and `.wpk` banks.
    *   **Code & Data**: Decompilation of `.luabin64` (Lua 5.1) and formatting of `.bin`, `.json`, `.xml`, `.svg`, and `.stringtable`.
*   **Adaptive Engineering Toolbar**: A dual-panel architecture featuring a clean header for core actions and an expanded technical suite (Mode, Grid, Breadcrumb).
*   **Intelligent Navigation**: Deep-seek engine with match highlighting and asynchronous "Go To" absolute path navigation.
*   **Contextual Tools**: Image Merger for texture sheets, Favorites for quick access, and a professional traceability menu for extraction and tracking.

### 🔊 Audio Bank Center
Deep inspection of Wwise audio banks (`.wpk`, `.bnk`), supporting event hierarchy resolution, linked master bank discovery, and direct media access.

## 🧰 Secondary Utilities (Quick Access)

*   **Asset Converter**: Unified multi-threaded engine for batch processing images (`.dds`, `.tex`) and audio (`.wem`, `.ogg`, `.mp3`).
*   **Audio Player**: Advanced session-based player featuring playlist management and YouTube streaming integration.
*   **Quick Notepad**: Integrated technical editor powered by AvalonEdit for quick note-taking during analysis.

## 📡 Monitoring Engine

AssetsManager includes a robust monitoring suite with specialized views for system and asset tracking:

*   **Dashboard**: Real-time telemetry for background services, PBE server status, and global system health overview.
*   **Asset Watcher**: Automated monitoring of local game files and plugins with version history and granular diff logging.
*   **Asset Tracker**: Persistent monitoring of specific assets with intelligent sequence management and extension fallback.
*   **Backups**: Comprehensive management of local game snapshots. Allows creating, refreshing, and organizing historical data from different installations with automatic version and role discovery.
*   **History**: Persistent registry of all past WAD comparisons and monitored remote data, allowing instant access to differential results.
*   **API Center**: Technical utility for querying official Riot APIs (Sales, Mythic Shop, Pass Rewards) with professional PNG export capabilities for community sharing.

## 🚀 Getting Started

### Prerequisites
*   [.NET 10.0 Runtime](https://dotnet.microsoft.com/es-es/download/dotnet/thank-you/runtime-desktop-10.0.2-windows-x64-installer) installed.

### Installation & Updates
1.  **Download**: Get the latest `AssetsManager_vX.X.X.X.zip` from the [Releases page](https://github.com/Neinndall/AssetsManager/releases).
2.  **Extract & Run**: Unzip to any folder and launch `AssetsManager.exe`.
3.  **Updates**: The integrated **Update Manager** will notify you of new versions for seamless clean or preserved installations.

## ⚙️ Configuration
All settings are managed via the `Settings` window and persisted in `config.json`, allowing for deep customization of monitoring frequencies and extraction preferences.

## 🤝 Contributing
Contributions are welcome! Feel free to fork the repo, submit **pull requests**, or open **issues** to discuss technical improvements or report bugs.

## 📄 License
This project is licensed under the [GNU General Public License v3.0](LICENSE).
