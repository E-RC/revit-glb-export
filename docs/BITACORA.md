# Bitácora de GLB Export for Revit

Estado y pendientes del proyecto, para retomarlo desde cualquier equipo (`git clone https://github.com/E-RC/revit-glb-export`). No contiene secretos.

## Estado (2026-10-07)
- Add-in .NET para Revit 2023 a 2027 (net48 para 2023/2024, net8 para 2025/2026, net10 para 2027). Una `GlbExport.dll` por versión, sin dependencias de terceros (a propósito: `System.Text.Json` choca con las DLL que Revit 2023/2024 ya carga).
- Repo público https://github.com/E-RC/revit-glb-export. Release **v1.0.0 sin firma** (instalador `.exe` + `.sha256`, compilado por GitHub Actions).
- Validado de punta a punta solo en **Revit 2024** con un modelo real (12,2 M de triángulos): exportación 48 s, GLB 20,7 MB. 2023, 2025, 2026 y 2027 compilan pero no se han ejecutado.
- Pruebas: `node --test "GlbExport/test/*.test.cjs"` (11) y `dotnet test tests/GlbExport.Tests` (9).

## Problema abierto: Smart App Control (SAC)
Windows 11 con SAC activo bloquea toda DLL sin firma de una CA pública (también las que compila pyRevit). Solo un release firmado carga en esos equipos.

## Firma: SignPath Foundation rechazó (2026-10-07)
- La solicitud se envió el 2026-10-07 y ese mismo día respondieron que **no la aprueban por ahora**: el programa exige visibilidad pública previa (estrellas, forks, contribuyentes, artículos o discusiones externas, respaldo institucional, actividad sostenida). Se puede reaplicar cuando el proyecto la tenga.
- Se quitó del README la sección de política de firma gratuita y se aclaró que los releases no están firmados.

## Opciones de firma evaluadas (decisión pendiente)
1. **SignPath de pago, plan Starter: USD 500 al año.** Incluye certificado EV de GlobalSign y 20 firmas de release al año. GlobalSign verifica la autoridad del solicitante en la organización (unos días). El CI ya está listo: el job `release` de `.github/workflows/ci.yml` firma solo cuando existen las variables `SIGNPATH_ORG_ID`, `SIGNPATH_PROJECT`, `SIGNPATH_POLICY` y el secreto `SIGNPATH_API_TOKEN`. Pasos: crear proyecto y política en app.signpath.io (artifact configuration que firme los `*.dll` de `dist/addin/<año>/`), cargar variables y secreto en GitHub (Settings > Secrets and variables > Actions), subir el tag `v1.0.1`, probar en un PC con SAC y quitar la advertencia de "sin firma".
2. **Azure Artifact Signing (ex Trusted Signing):** solo organizaciones de EE.UU., Canadá, UE y Reino Unido; Chile no califica.
3. **Certificado OV o EV comercial** (unos USD 200 a 500 al año; clave en token/HSM o firma en la nube): exige adaptar el CI.
4. **Soporte informático:** pasar de SAC a una política App Control administrada que confíe en un certificado de la empresa, o desactivar SAC (irreversible).
5. **Ruta Dynamo (sin DLL nueva):** reescribir el exportador como script de Dynamo/Python. Dynamo y su motor Python vienen firmados con Revit, así que funciona bajo SAC sin certificado. La prueba en vivo ya mostró que cargar el código desde memoria no lo bloquea SAC.
6. **Reaplicar a SignPath Foundation** cuando haya tracción.
## Otros pendientes
- Probar el add-in en Revit 2023, 2025, 2026 y 2027 y con vínculos.
- Probar el instalador `.exe` en un PC limpio (sin Node.js).
- El diálogo está solo en español.

## Cómo reconstruir el entorno
Requiere SDK de .NET 10 (compila 2027), Node 18+ y Inno Setup 6 (solo para el instalador local). `.\build.ps1` compila 2023-2027 en `dist\addin`; `.\install.ps1` instala para el usuario actual; `.\uninstall.ps1` quita. Detalle en el README.

## Historia
- 2026-10-07: port del exportador pyRevit/IronPython a C#, instalador, repo público, release v1.0.0, solicitud a SignPath enviada y rechazada el mismo día (falta de reputación), README corregido.
