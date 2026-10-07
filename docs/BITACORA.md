# Bitácora de GLB Export for Revit

Estado y pendientes del proyecto, para retomarlo desde cualquier equipo (`git clone https://github.com/E-RC/revit-glb-export`). No contiene secretos.

## Estado (2026-10-07)
- Add-in .NET para Revit 2023 a 2027 (net48 para 2023/2024, net8 para 2025/2026, net10 para 2027). Una `GlbExport.dll` por versión, sin dependencias de terceros (a propósito: `System.Text.Json` choca con las DLL que Revit 2023/2024 ya carga).
- Repo público https://github.com/E-RC/revit-glb-export. Release **v1.0.0 sin firma** (instalador `.exe` + `.sha256`, compilado por GitHub Actions).
- Validado de punta a punta solo en **Revit 2024** con un modelo real (12,2 M de triángulos): exportación 48 s, GLB 20,7 MB. 2023, 2025, 2026 y 2027 compilan pero no se han ejecutado.
- Pruebas: `node --test "GlbExport/test/*.test.cjs"` (11) y `dotnet test tests/GlbExport.Tests` (9).

## Problema abierto: Smart App Control (SAC)
Windows 11 con SAC activo bloquea toda DLL sin firma de una CA pública (también las que compila pyRevit). Solo un release firmado carga en esos equipos. Por eso se pidió firma gratuita a **SignPath Foundation**.

## Solicitud a SignPath
- Enviada el **2026-10-07** por https://signpath.org/apply (proyecto "GLB Export for Revit", mantenedor individual, GitHub Actions). Respuesta esperada por correo al solicitante; puede tardar de días a semanas.
- Riesgo: el repo es nuevo y sin reputación (estrellas, descargas); pueden rechazar o pedir más datos.
- La Download URL enviada fue `.../releases`; si exigen mención a SignPath en esa página, poner la atribución en las notas del release (el README ya la tiene: sección "Code signing policy").

## Qué hacer cuando llegue la respuesta
**Si aprueban:**
1. Activar la cuenta desde el correo e ingresar a https://app.signpath.io. El ID de organización es el UUID de la URL (`app.signpath.io/Web/<organization-id>/...`).
2. En SignPath: crear o confirmar el proyecto, vincularlo al repo de GitHub y crear una política de firma (p. ej. `release-signing`) con un *artifact configuration* que firme los `*.dll` de `dist/addin/<año>/` (zip del artefacto `dlls-unsigned`).
3. En GitHub (Settings > Secrets and variables > Actions):
   - Variables: `SIGNPATH_ORG_ID`, `SIGNPATH_PROJECT` (slug del proyecto), `SIGNPATH_POLICY` (slug de la política).
   - Secreto: `SIGNPATH_API_TOKEN` (lo crea el usuario en su perfil de SignPath; no pasarlo por chat ni escribirlo en archivos).
4. Subir un tag nuevo (`git tag v1.0.1; git push origin v1.0.1`). El job `release` de `.github/workflows/ci.yml` se activa solo cuando `SIGNPATH_ORG_ID` existe: sube las DLL, las firma, recompila el instalador con las firmadas y publica el release.
5. Probar en un PC con SAC: instalar el `.exe`, reiniciar Revit, comprobar la pestaña **GLB** y exportar una vista 3D. Anotar el resultado aquí.
6. Quitar la advertencia "NOT code-signed" de las notas y del README.

**Si rechazan o piden más:** responder con datos de uso real (descargas, estrellas, quién lo usa). Alternativas: pedir a soporte informático que permitan al editor en SAC o que firmen las DLL con el certificado de la empresa; o certificado de pago (OV) / Azure Trusted Signing.

## Otros pendientes
- Probar el add-in en Revit 2023, 2025, 2026 y 2027 y con vínculos.
- Probar el instalador `.exe` en un PC limpio (sin Node.js).
- El diálogo está solo en español.

## Cómo reconstruir el entorno
Requiere SDK de .NET 10 (compila 2027), Node 18+ y Inno Setup 6 (solo para el instalador local). `.\build.ps1` compila 2023-2027 en `dist\addin`; `.\install.ps1` instala para el usuario actual; `.\uninstall.ps1` quita. Detalle en el README.

## Historia
- 2026-10-07: port del exportador pyRevit/IronPython a C#, instalador, repo público, release v1.0.0, solicitud a SignPath enviada, política de firma agregada al README.
