# Validation — patch-only build 0.2

Performed in an isolated game copy; no upload or public release was performed.

- Independent copy matches every fresh Steam file
- Automatic Steam detection found the fresh installation
- Clean original DLL and assets reconstruct exactly; both local caches match
- Patched game created a fresh world and reached gameplay
- Undo restores every original game file and removes the added helper
- Fresh Steam installation unchanged
- Normal saves unchanged

- Reconstructed asset bundle SHA-256: F2D40586E85DB1CC7245A187FBDDC7B38A0B13C83C1116BB31FCE7664429D293 (identical to prior optimized build).
- Reconstructed game DLL SHA-256: 03213C79AD0BA979D530513C1A6F1585415E7A81497F6F8FD9ABD60E5A89EFB2 (identical to prior optimized build).
- Both locally generated catalogs match prior catalog hashes exactly. The standalone cache-process test finished in 22.8 seconds; the cache itself was generated in about 11.5 seconds. This is one-time setup, not a gameplay loading benchmark.
- An earlier cache-timeout failure restored all original game file hashes automatically; the corrected cache builder passed the final tests.
- The installer package and source archive were checked for full game files and generated caches.

The complete clean-original-DLL input path was verified on October 28, 2026 using the actual Desktop sharing EXE. Install took 163.19 seconds, the fresh-world gameplay smoke-test process took 28.47 seconds, and Undo took 1.62 seconds. Every original game file in the test copy was restored exactly. The source Steam installation and normal saves were unchanged. Full dungeon gameplay, long-session stability and compatibility with other mods remain unverified.
