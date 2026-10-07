# GLB Export for Revit

A Revit add-in that exports the active 3D view to a lightweight **GLB** for WebXR / Meta Quest and web viewers. Built for real architecture models: an 11 M triangle, 558 MB export went down to 1.4 M triangles and 18 MB.

*Add-in de Revit que exporta la vista 3D activa a un GLB liviano para WebXR / Meta Quest. Instrucciones en español más abajo.*

## What it does
- Exports only what is visible in the active 3D view, including linked models.
- Real instancing (`EXT_mesh_gpu_instancing`) for repeated elements (doors, furniture, ...).
- Per-category simplification (how much to keep, max error in cm, locked borders) with presets: *Quest light*, *Balanced*, *No optimization*.
- Vertex colors instead of one material per color (far fewer draw calls), consistent face winding, single-sided faces where safe.
- Floors, terrain, stairs, ramps, doors and windows keep their own named meshes (useful for walk-through viewers).
- meshopt compression.

## Requirements
- Windows 10/11 and **Revit 2023, 2024, 2025, 2026 or 2027** (one build per version: .NET Framework 4.8, .NET 8 and .NET 10).
- Node.js 18+ (the installer adds the LTS version through `winget` if missing).
- **If Windows has Smart App Control on, you need a signed release** (see below).

## Install
**Option A: installer.** Download `GLBExport-Setup-x.y.z.exe` from [Releases](../../releases), check its SHA-256 against the `.sha256` file and run it. No admin rights needed. It copies the add-in, registers it for every Revit 2023-2027 it finds, and installs Node.js and the npm packages if needed.

**Option B: PowerShell** (build from source, needs the .NET 10 SDK):
```powershell
git clone https://github.com/E-RC/revit-glb-export.git
cd revit-glb-export
.\build.ps1
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Then **restart Revit**. A **GLB** tab appears with the **GLB Export** button.

### Smart App Control and code signing
Windows 11 *Smart App Control* (SAC) blocks any DLL that is not signed by a publicly trusted certificate, and Revit add-ins are DLLs. On a PC with SAC on (common on new, managed PCs; it cannot be turned back on once disabled, and IT often controls it):
- **Signed releases load normally.** Releases are signed with a publicly trusted certificate through [SignPath Foundation](https://signpath.org) when available (see the release notes: each release says whether it is signed).
- **Unsigned builds, including anything you build yourself, are blocked** and Revit shows a "part of this app has been blocked" notice. Ask IT to allow the signer, use a signed release, or disable SAC (Settings > Privacy & security > Windows Security > App & browser control).
Without SAC (or with a signed release) nothing else is needed.

## Use
1. Open a 3D view.
2. GLB tab > **GLB Export**. Pick the output file and a profile.
3. Adjust categories if needed and press **Export**. It takes a few minutes and Revit stays busy.

Your last settings are kept in `%APPDATA%\Archiplan\GlbExport\last.profile`.

## Uninstall
Windows *Apps & features* > GLB Export for Revit, or `.\uninstall.ps1` (`-Deps` also removes the npm packages in `%LOCALAPPDATA%\ArchiGlb`). Node.js is left alone.

## How it works
`SceneExporter.cs` runs Revit's `CustomExporter` in two passes and writes `scene.json` + `data.bin`. `GlbExport/src/build.cjs` (Node, [glTF-Transform](https://gltf-transform.dev) + meshoptimizer) merges, simplifies and writes the GLB. Rules live in `GlbExport/rules.default.json`. The add-in has no third-party .NET dependencies, so it cannot clash with other add-ins.

## Development
```powershell
node --test "GlbExport/test/*.test.cjs"     # converter
dotnet test tests/GlbExport.Tests           # options and JSON writer
.\build.ps1 -Years 2024                      # one Revit version; no argument builds 2023-2027
```
Installer: install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then `ISCC.exe /DAppVersion=1.0.0 installer\GLBExport.iss`. Pushing a tag `vX.Y.Z` builds, signs (if SignPath is configured) and publishes it through GitHub Actions.

## Known limits
Tested against the Revit 2024 and 2025 API surface at compile time; reports from 2023, 2026 and 2027 are welcome. The dialog is in Spanish. Large models take minutes and block Revit while exporting.

---

## En español
**Instalar:** descarga el `.exe` desde *Releases* y ejecútalo (no pide administrador). Registra el add-in en todas las versiones de Revit 2023 a 2027 que encuentre e instala Node.js si falta. Reinicia Revit: aparece la pestaña **GLB** con el botón **GLB Export**.
**Smart App Control:** si está activo, Windows bloquea las DLL sin firma de una CA pública. Usa un release firmado, pide a TI que permitan al firmante o desactiva SAC.
**Usar:** abre una vista 3D, pulsa el botón, elige perfil y archivo de salida, y exporta. Solo se exporta lo visible en la vista.
**Desinstalar:** *Aplicaciones* de Windows, o `uninstall.ps1`.

## License
MIT. See [LICENSE](LICENSE). Models you export are yours; nothing is uploaded anywhere.
