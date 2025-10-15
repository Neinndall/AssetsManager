<div align="center">
  <img src="https://github.com/Neinndall/AssetsManager/blob/main/AssetsManager/Resources/Img/logo.ico" alt="AssetsManager Logo" width="150">
</div>

## ✅ APP Reliability
You can review and check the sha-256 in github and virustotal for checking if both numbers matches.

*   **Latest Version v2.4.1.0:** **[VirusTotal details and scans](https://www.virustotal.com/gui/file/3f87d108f8ba0a426460d57ba028e157919af38b15c87a6dbda898353d9d7992/details)** 

## 🛠️ AssetsManager

AssetsManager is a tool for League of Legends enthusiasts who need to analyze, manage, and track changes to game assets from PBE server updates. It goes beyond simple downloading, offering a powerful suite of features for deep asset analysis, a 3D model viewer, exploration, and monitoring for changes.

## ✨ Key Features

*   **Automatic Asset Downloading**: Automatically detects and downloads new assets from PBE updates.
*   **Advanced WAD Comparator**: Compares WAD files between different versions to identify new, modified, or deleted assets.
*   **Wad Explorer**: Explores WAD archives with a file tree interface and previews dozens of formats.
*   **3D Model Viewer**: Inspects 3D models (.skn), skeletons (.skl), and animations (.anm) with a built-in viewer.
*   **Asset Monitoring**: Tracks remote assets, JSON files, and the PBE server status for real-time updates.
*   **Version Management**: Easily manages and downloads different versions of the League of Legends client and its plugins.
*   **Smart Extraction and Saving**: Extracts raw assets or saves them in more user-friendly formats (e.g., .tex to .png, .bin to .json).
*   **Audio Bank Analysis**: Explores audio banks (.wpk, .bnk), visualizes the event hierarchy with their names, and plays the associated sounds.

## 🦾 Advanced Functionality

### Monitoring Suite (`MonitorWindow`)

AssetsManager now includes a powerful suite of tools to automatically track changes in game assets without manual intervention.

*   **File Watcher:** Monitor a list of remote JSON files for any updates. When a change is detected, the app automatically saves the old and new versions and logs the difference, allowing you to view the changes at any time.
*   **Asset Tracker:** Keep a persistent list of specific assets you want to track. The tool will periodically check their status (e.g., "OK", "Not Found", "Pending") in the background. It even includes fallback logic for assets with multiple possible extensions (like `.jpg` and `.png`).
*   **History View:** All detected changes from the File Watcher are saved in a persistent history. You can browse past changes, view the diffs, and manage the history log.
*   **Manage Versions:** A new tool that natively integrates with Riot's APIs to fetch and manage different client and game versions. It allows you to download version manifests, plugins, and even full game clients for specific PBE patches.

### 3D Model Viewer (`ModelWindow`)

Explore League of Legends 3D assets like never before.

*   **Model & Skeleton Loading:** Load `.skn` (mesh) and `.skl` (skeleton) files to view character and environment models.
*   **Animation Playback:** Apply `.anm` (animation) files to a loaded skeleton to see the model come to life with full skinning support.
*   **Scene Control:** Manipulate the 3D camera, manage loaded parts, and inspect model geometry.

## 🚀 Getting Started

### Prerequisites

*   [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-8.0.8-windows-x64-installer) (or higher) installed on your system.

### Installation

1.  **Download the latest release:** Visit the [Releases page](https://github.com/Neinndall/AssetsManager/releases) (replace with your actual releases URL) and download the `AssetsManager.zip` file.
2.  **Extract the contents:** Unzip the downloaded file to your desired location (e.g., `C:\AssetsManager`).
3.  **Run the application:** Navigate to the extracted folder and run `AssetsManager.exe`.

## 📖 Usage

1.  **Configure Settings:** Open the `Settings` tab to set up your preferences, including hash synchronization, auto-copy, and backup options.
2.  **Select Directories:** In the `Home` tab, specify your "New Hashes Directory" and "Old Hashes Directory".
3.  **Start Download:** Click the "Start Download" button to begin the asset extraction and download process. The application will compare hashes and download only the necessary files.
5.  **Explore & Monitor:** Use the `Explorer`, `Comparator`, `Monitor`, and `Model Viewer` tabs to access the advanced features of the application.

## ⚙️ Configuration

All application settings are managed through a dedicated `Settings` window and persisted in the `config.json` file. This allows for deep customization of the application's behavior to fit your workflow. Key configuration areas include setting default paths, automating core processes like hash management and backups, and fine-tuning the behavior of advanced features such as background monitoring services and application updates.

## 🤝 Contributing

Contributions are welcome! If you have suggestions for improvements, bug reports, or want to contribute code, please feel free to:

1.  Fork the repository and submit a [pull requests](https://github.com/Neinndall/AssetsManager/pulls). 
2.  Open an [issues](https://github.com/Neinndall/AssetsManager/issues) to discuss your ideas or report bugs.

Please ensure your code adheres to the project's existing style and conventions.

## 📄 License

This project is licensed under the [GNU General Public License v3.0](LICENSE).