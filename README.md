# GLB Export for Revit

A [pyRevit](https://github.com/pyrevitlabs/pyRevit) button that exports the active 3D view to a lightweight **GLB** for WebXR / Meta Quest and web viewers. Built for real architecture models: an 11 M triangle, 558 MB export went down to 1.4 M triangles and 18 MB.

*Botón de pyRevit que exporta la vista 3D activa a un GLB liviano para WebXR / Meta Quest. Instrucciones en español más abajo.*

## What it does
- Exports only what is visible in the active 3D view, including linked models.
- Real instancing (`EXT_mesh_gpu_instancing`) for repeated elements (doors, furniture, ...).
- Per-category simplification (how much to keep, max error in cm, locked borders) with presets: *Quest light*, *Balanced*, *No optimization*.
- Vertex colors instead of one material per color (far fewer draw calls), consistent face winding, single-sided faces where safe.
- Floors, terrain, stairs, ramps, doors and windows keep their own named meshes (useful for walk-through viewers).
- meshopt compression.

## Requirements
- Windows 10/11, Revit 2024 (tested). Revit 2025 and later: untested, reports welcome.
- [pyRevit](https://github.com/pyrevitlabs/pyRevit) (the installer adds it if missing).
- Node.js 18+ (the installer adds the LTS version if missing).

## Install
**Option A: installer.** Download `GLBExport-Setup-x.y.z.exe` from [Releases](../../releases), check its SHA-256 against the `.sha256` file, run it. No admin rights needed. It installs pyRevit and Node.js through `winget` only if they are missing.

> The `.exe` is not code-signed yet, so Windows SmartScreen may warn ("More info" > "Run anyway"). If you prefer, use option B and read the script first.

**Option B: PowerShell.**
```powershell
git clone https://github.com/E-RC/revit-glb-export.git
cd revit-glb-export
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Then **restart Revit** (do not use *Reload* in pyRevit). A **GLB** tab appears with the **GLB Export** button.

## Use
1. Open a 3D view.
2. GLB tab > **GLB Export**. Pick the output file and a profile.
3. Adjust categories if needed and press **Export**. It takes a few minutes and Revit stays busy.

Your last settings are kept in `%APPDATA%\Archiplan\GlbExport\last.json`.

## Uninstall
Windows *Apps & features* > GLB Export for Revit, or `.\uninstall.ps1` (`-Deps` also removes the npm packages in `%LOCALAPPDATA%\ArchiGlb`). Node.js and pyRevit are left alone.

## How it works
`exporter.py` (IronPython, `CustomExporter`, two passes) writes `scene.json` + `data.bin`. `GlbExport/src/build.cjs` (Node, [glTF-Transform](https://gltf-transform.dev) + meshoptimizer) merges, simplifies and writes the GLB. Rules live in `GlbExport/rules.default.json`.

## Development
```powershell
node --test "GLBExport.extension/GlbExport/test/*.test.cjs"          # converter
cd "GLBExport.extension/GLB.tab/Export.panel/GLB Export.pushbutton"; python test_options.py   # options logic
```
Build the installer: install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then `ISCC.exe /DAppVersion=1.0.0 installer\GLBExport.iss`. Pushing a tag `vX.Y.Z` builds and publishes it through GitHub Actions.

## Known limits
Revit 2025+ and linked models are untested. The dialog is in Spanish. Large models take minutes and block Revit while exporting.

---

## En español
**Instalar:** descarga el `.exe` desde *Releases* y ejecútalo (no pide administrador; instala pyRevit y Node.js solo si faltan), o usa `install.ps1`. Reinicia Revit: aparece la pestaña **GLB** con el botón **GLB Export**.
**Usar:** abre una vista 3D, pulsa el botón, elige perfil y archivo de salida, y exporta. Solo se exporta lo visible en la vista.
**Desinstalar:** *Aplicaciones* de Windows, o `uninstall.ps1`.

## License
MIT. See [LICENSE](LICENSE). Models you export are yours; nothing is uploaded anywhere.
