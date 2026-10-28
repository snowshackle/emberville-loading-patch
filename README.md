# Emberville Loading Patch

An experimental, unofficial loading patch maintained by **snowshackle**. One Windows installer with automatic Steam detection, Install, Undo, and separate test saves.

Unofficial loading experiment for **Emberville Demo Alpha 0.20.18.239 / Unity 6000.1.17f1**, Windows x64. Requires the matching game already installed.

## Download

Get `Emberville-Patch-Only-Installer.exe` and its SHA-256 checksum from [Releases](https://github.com/snowshackle/emberville-loading-patch/releases). The EXE is approximately 25 MB. Game files are rebuilt locally from your own matching installation.

## Using the installer

1. Close the game and run `Emberville-Patch-Only-Installer.exe`.
2. The installer searches Steam libraries automatically. Use **Browse** for a separate copy.
3. Click **Install patch**. Allow several minutes and roughly **12 GB of temporary free space**. It reconstructs the modified files from your installation, backs up originals, then runs the game briefly in the background to generate map caches. That process closes automatically.
4. Use **Launch game**. Test saves are stored separately in `EmbervillePatch/TestUserData`. Existing normal saves are copied for testing, not overwritten.
5. Close the game and click **Undo patch** to restore the backed-up game files. Test saves, generated caches, logs and backups remain available.

Use Undo before updating the game or applying another modification. An unknown build or changed backup is rejected. If an older full-file installer is already installed, use that installer's Undo first. Protected installation folders may require running the installer as administrator; a writable separate copy also works.

## What is distributed

- Our installer, assembly-editing instructions and optimization helper.
- COPY/LITERAL binary differences for the asset bundle's decoded files; reconstruction requires the hash-matched original bundle.
- A small private Python runtime with LZ4, and Mono.Cecil, with third-party licenses.

**No complete game DLLs, UnityPlayer, executable, asset bundles, textures, sprite sheets, saves or map catalogs are included.** Map catalogs are derived locally from the recipient's game. The installer needs no Python/.NET SDK download or network connection on the recipient's computer. It uses Windows .NET Framework for its UI and patch tools.

The changes include loading-priority adjustment, unused particle-mesh cleanup, loading-progress/inventory fixes, faster WFC selection and map rendering, lightweight map metadata, and selective region-prefab loading. This remains a test build; complete dungeon gameplay and mod compatibility are not established.

## Validation

On October 28, 2026, the actual sharing EXE was tested against an independent copy of a fresh Steam download. Automatic detection found the Steam installation. The complete clean-original-DLL patch path passed, and the reconstructed DLL, asset bundle and locally generated map catalogs matched the expected hashes exactly.

- Install: **163.19 seconds** (one-time patch setup, not a game loading benchmark).
- The patched game created a new world and reached gameplay; the automated smoke-test process completed in **28.47 seconds**.
- Undo: **1.62 seconds**. Every original game file matched its pre-install SHA-256; the added helper DLL was removed.
- The fresh Steam installation and normal saves remained unchanged.

This confirms the tested installer workflow and a short gameplay smoke test. It does not establish full dungeon gameplay, long-session stability, or compatibility with other mods or game versions. See `VALIDATION.md` for details.

## Rebuild the installer

The source archive includes the `Package` directory with patch resources and redistributable tools, not game files. Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
```

This rebuilds the single EXE using Windows .NET Framework's compiler and writes a SHA-256 sidecar. The build script rejects unexpected package entries and known game-file names. Do not add game installations, generated `payload` directories, caches, logs, or saves to a public repository.

`helper/` contains the optimization source; `assembly/` contains the three Mono.Cecil edit stages. These sources require local game references when recompiling the helper. `DeltaBuilder.cs` creates asset deltas for development; `bundle_patch.py` reconstructs and recompresses the recipient's bundle. Changing optimization behavior requires regenerating the corresponding hashes/deltas and repeating validation; rebuilding the wrapper alone does not update its embedded tools.

## Feedback

Use Issues to report installer failures or gameplay regressions. Include the game version, Windows version, steps to reproduce, and the relevant error text. Remove personal paths or identifying details before sharing log excerpts.

## Credits and third-party components

Emberville and its original content belong to their respective owners. This project is unofficial and is not endorsed by the developers. Third-party runtime licenses are included in `Package/licenses`. No rights to the original game are granted by this project.
