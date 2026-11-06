# Quita GLB Export. Node.js no se toca; con -Deps tambien borra las dependencias npm;
# con -PyRevitDir <carpeta> quita tambien <carpeta>\ArchiGlb.extension.
param([switch]$Deps, [string]$PyRevitDir)
$ErrorActionPreference = 'Stop'
foreach ($y in 2023..2027) {
    $f = "$env:APPDATA\Autodesk\Revit\Addins\$y\GLBExport.addin"
    if (Test-Path $f) { Remove-Item -LiteralPath $f -Force; Write-Host "Eliminado $f" }
}
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GLBExport'
if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force; Write-Host "Eliminado $dest" }
if ($PyRevitDir) {
    $ext = Join-Path $PyRevitDir 'ArchiGlb.extension'
    if (Test-Path $ext) { Remove-Item -LiteralPath $ext -Recurse -Force; Write-Host "Eliminado $ext" }
}
if ($Deps) {
    Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'ArchiGlb') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Dependencias eliminadas.'
}
Write-Host 'Reinicia Revit.'
