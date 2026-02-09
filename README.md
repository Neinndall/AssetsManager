<div align="center">
  <img src="https://github.com/Neinndall/AssetsManager/blob/main/AssetsManager/Resources/Img/logo.ico" alt="Logo" width="100">
</div>

## 🛠️ AssetsManager

[![Latest Release](https://img.shields.io/github/v/release/Neinndall/AssetsManager?color=yellow&logo=github&logoColor=white&label=Release&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![Downloads](https://img.shields.io/github/downloads/Neinndall/AssetsManager/total?color=blue&logo=github&logoColor=white&label=Downloads&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![License](https://img.shields.io/github/license/Neinndall/AssetsManager)](https://github.com/Neinndall/AssetsManager/blob/main/LICENSE)

AssetsManager is a powerful tool designed for League of Legends enthusiasts who need to analyze, manage, and track changes to game assets from PBE updates. It offers a comprehensive suite of features for deep asset inspection, 3D model visualization, archive exploration, and real-time monitoring.

## 🏛️ Core Modules

*   **Advanced WAD Comparator**: A high-speed comparison engine featuring **Fast Mode** (Reflection-based) to identify new, modified, or deleted assets in seconds.
*   **Archive Explorer**: A professional navigation suite for the WAD game files with advanced toolbar tools:
    *   **Premium Gallery (Grid View)**: High-performance visual explorer with asynchronous thumbnail generation and rich metadata badges.
    *    **Load Backups**: Ability to load your previous comparison backups from WAD Explorer.
    *   **Image Merger**: Specialized tray to "collect" and composite multiple textures into professional contact sheets.
    *   **Quick Access (Favorites)**: Persistent system to pin frequently used assets for instant cross-session navigation.
    *   **Intelligent Search**: Deep-seek engine with real-time filtering and asynchronous "Go To" capabilities.
*   **3D Workspace**: Fully integrated HelixToolkit viewport for visualizing champion models (`.skn`), animations (`.anm`), and environment MapGeometry (`.mapgeo`).
*   **Version Management**: Native integration with Riot APIs to manage manifests, plugins, and full game client downloads via a parallel RMAN engine.
*   **Audio Bank Center**: Deep inspection of Wwise audio banks (`.wpk`, `.bnk`), supporting event hierarchy resolution and direct media access.

## 🧰 Integrated Utility Suite (Home Apps)

*   **Universal Converter**: A unified multi-threaded engine for batch processing.
    *   **Images**: `.dds`, `.tex` → PNG/JPG.
    *   **Audio**: `.wem`, `.ogg`, `.mp3`, `.wav` → OGG/WAV/MP3.
*   **Professional Audio Player**: Advanced session-based player featuring playlist management, real-time volume control, and YouTube streaming integration.
*   **Smart Notepad**: Integrated technical editor powered by AvalonEdit for quick note-taking during asset analysis.

## 🦾 High-Performance Architecture

*   **Parallel Sync Engine**: Simultaneous multi-file acquisition with atomic `.tmp` protection to guarantee local database integrity.
*   **Optimized Hash Engine**: Re-engineered startup sequence with linear-seek parsing and proactive memory allocation for near-instant hash loading.
*   **Blake3 Cryptography**: Implementation of Pure C# Blake3 for high-speed integrity verification across massive asset sets.
*   **Chromeless HUD Design**: A hardware-accelerated UI with a 40px HUD title bar and professional technical aesthetics.

## 📡 Monitoring Engine (`MonitorWindow`)

*   **Live Dashboard**: Real-time telemetry for background services, PBE server status, and global system health.
*   **File Watcher**: Automated tracking of remote JSON updates with version history and granular diff logging.
*   **History**: Storage where you will find your comparison backups and monitored remotes.
*   **Asset Tracker**: Persistent monitoring of specific assets with intelligent sequence management and extension fallback.
*   **Comparison History**: Persistent registry of all past WAD comparisons, allowing instant access to cached results and differential data.
*   **Backup Manager**: Specialized tool to create and manage local snapshots of the PBE file system, ensuring data safety across patches.
*   **API Center**: Technical utility for querying official Riot APIs for sales, Mythic Shop rotation, and player metadata.

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
