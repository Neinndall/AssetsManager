<div align="center">
  <img src="https://github.com/Neinndall/AssetsManager/blob/main/AssetsManager/Resources/Img/logo.ico" alt="Logo" width="100">
</div>

## 🛠️ AssetsManager

[![Latest Release](https://img.shields.io/github/v/release/Neinndall/AssetsManager?color=yellow&logo=github&logoColor=white&label=Release&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![Downloads](https://img.shields.io/github/downloads/Neinndall/AssetsManager/total?color=blue&logo=github&logoColor=white&label=Downloads&style=flat)](https://github.com/Neinndall/AssetsManager/releases)
[![VirusTotal](https://img.shields.io/badge/VirusTotal-0/72-brightgreen?logo=virustotal&logoColor=white&style=flat)](https://www.virustotal.com/gui/file/2a03585d90d5299a53ea16ba139e4c55d7fc4d412b443c43455a9d3acded2da2/detection)

AssetsManager is a powerful tool designed for League of Legends enthusiasts who need to analyze, manage, and track changes to game assets from PBE updates. It offers a comprehensive suite of features for deep asset inspection, 3D model visualization, archive exploration, and real-time monitoring.

## ✨ Key Features

*   **Advanced WAD Comparator**: Compares WAD files between different versions to identify new, modified, or deleted assets with automated extraction and smart data conversion.
*   **Powerful Archive Explorer**: Navigate game files with a modern tree interface or a **Premium Gallery (Grid View)** featuring floating cards, hero thumbnails, and rich metadata indicators.
*   **3D Model Viewer**: Fully integrated viewport to visualize champion models (`.skn`), animations (`.anm`), and MapGeometry environments (`.mapgeo`) with real-time skinning support.
*   **Real-time Monitoring**: Automated suite to track remote JSON files, CDN changes, and PBE server status with instant system notifications.
*   **Version Management**: Natively integrates with Riot APIs to manage manifests, plugins, and full game client downloads for specific patches.
*   **Documentation Center**: A built-in, professionally designed guide featuring module overviews and pro tips to master the application.
*   **Audio Bank Center**: Explore audio banks (`.wpk`, `.bnk`), visualize complex event hierarchies with resolved names, and play associated sounds.

## 🦾 Advanced Functionality

### Monitoring Suite (`MonitorWindow`)

AssetsManager includes a professional suite of tools to automatically track changes in game assets without manual intervention.

*   **Monitor Dashboard:** A central hub providing a real-time status overview of all background services, PBE status, and system health.
*   **File Watcher:** Monitors remote JSON files for updates. When a change is detected, the app automatically saves versions and logs the difference for comparison.
*   **Asset Tracker:** Keeps a persistent list of specific assets to track, checking their status periodically with intelligent fallback logic for extensions.
*   **History View:** All detected changes are saved in a persistent history where you can browse past updates and view detailed diffs.
*   **Backups:** Section to manage and control local backups of your League of Legends PBE file system.
*   **API Center:** Powerful utility to query League of Legends APIs for real-time sales, Mythic Shop items, and player information.

### 3D Visualization & Extraction

*   **Animation Playback:** Apply `.anm` files to loaded skeletons to see models in motion with high-performance skinning.
*   **Smart Saving:** Automatically converts raw game formats (like textures to `.png` or binary data to `.json`) while preserving the original folder structure during extraction.
*   **3D Models & MapGeometry:** Inspect 3D models of champions `.skn` with their animations `.anm` and MapGeometry Environments `.mapgeo`.
*   **Animation Playback:** Apply `.anm` (animation) files to a loaded skeleton to see the model come to life with full skinning support.
*   **Scene Control:** Manipulate the 3D camera, manage loaded parts, and inspect model geometry.

## 🚀 Getting Started

### Prerequisites

*   [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-8.0.8-windows-x64-installer) (or higher) installed on your system.

### Installation

1.  **Download the latest release:** Visit the [Releases page](https://github.com/Neinndall/AssetsManager/releases) and download the `AssetsManager.zip` file.
2.  **Extract the contents:** Unzip the file to your desired location.
3.  **Run the application:** Launch `AssetsManager.exe` to start exploring.

## ⚙️ Configuration

All application settings are managed through a dedicated `Settings` window and persisted in `config.json`. This allows for deep customization, from setting default paths to fine-tuning background monitoring frequency and update behavior.

## 🤝 Contributing

Contributions are welcome! If you have suggestions for improvements, bug reports, or want to contribute code, please feel free to:

1.  Fork the repository and submit a [pull requests](https://github.com/Neinndall/AssetsManager/pulls). 
2.  Open an [issues](https://github.com/Neinndall/AssetsManager/issues) to discuss your ideas or report bugs.

Please ensure your code adheres to the project's existing style and conventions.

## 📄 License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
