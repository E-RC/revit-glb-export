# Quita GLB Export. Node.js y pyRevit no se tocan; con -Deps tambien borra las dependencias npm.
param([switch]$Deps)
$ErrorActionPreference = 'Stop'
$ext = Join-Path $env:APPDATA 'pyRevit\Extensions\GLBExport.extension'
if (Test-Path $ext) { Remove-Item -LiteralPath $ext -Recurse -Force; Write-Host "Eliminado $ext" }
if ($Deps) {
    Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'ArchiGlb') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Dependencias eliminadas.'
}
Write-Host 'Reinicia Revit.'
