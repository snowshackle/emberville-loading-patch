# v0.2.0 — Experimental patch-only installer

First public test release for Emberville Demo Alpha 0.20.18.239 (Unity 6000.1.17f1), Windows x64.

## Download and install

Download **Emberville-Patch-Only-Installer.exe**, close the game, and select Install. Automatic Steam detection and a manual folder picker are included. Use a separate game copy for testing. Allow several minutes and 12 GB of temporary free space. Undo restores the original game files; test saves and logs remain separate.

This release contains patch instructions, our helper and redistributable tools. It does not include complete game assemblies, bundles, textures, sprites or saves. Map metadata is generated from the recipient's game during installation.

## Changes

- Adjust background loading priority during loading.
- Remove unused particle-renderer mesh references.
- Improve loading-progress handling and inventory loading.
- Optimize WFC selection, tile batching, map images and prop lookup.
- Separate map metadata from full gameplay prefabs.
- Load the prefabs required for the selected region.

## Verified on a fresh Steam copy

- Automatic game detection and the complete clean-install path passed.
- Rebuilt DLL, assets and generated metadata matched the expected hashes.
- Installation took 163.19 seconds; Undo took 1.62 seconds on the test machine.
- The patched game created a fresh world and reached gameplay.
- Every original game file was restored exactly; normal saves and the Steam installation stayed unchanged.

These are installer timings, not promised gameplay loading times. Full dungeon gameplay, long sessions and compatibility with other mods remain unverified. Unknown game builds are rejected. Treat this as an experimental prerelease.

Installer SHA-256:
`87FF7AB873F5EC98510998DFB4B1B2C6DC43C28DD6A71AA2B2467FC377F98657`
