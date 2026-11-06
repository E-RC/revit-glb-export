<#
 Instala GLB Export (add-in de Revit 2023..2027) para el usuario actual. No requiere administrador.
   - Node.js 18+: si falta, instala la version LTS con winget.
   - Dependencias npm (fijadas por package-lock.json) en %LOCALAPPDATA%\ArchiGlb.
   - Copia el add-in a %LOCALAPPDATA%\Programs\GLBExport y registra GLBExport.addin
     en %APPDATA%\Autodesk\Revit\Addins\<anio> para cada Revit instalado.
   - Con -PyRevitDir <carpeta>: en vez de registrar el add-in, instala una extension de pyRevit
     (<carpeta>\ArchiGlb.extension) con el boton GLB Export dentro de la pestana -Tab / panel -Panel
     (por defecto Archi_Tools / Exportacion). No crea la pestana GLB ni el .addin.
 Uso: .\install.ps1 [-Source <carpeta con <anio>\GlbExport.dll y GlbExport\>]   (por defecto dist\addin)
      .\install.ps1 -PyRevitDir C:\ArchiTools [-Tab Archi_Tools] [-Panel Exportacion] [-SkipNpm]
#>
[CmdletBinding()]
param([string]$Source, [string]$PyRevitDir, [string]$Tab = 'Archi_Tools', [string]$Panel = 'Exportacion', [switch]$SkipNpm)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path   # $PSScriptRoot no sirve como valor por defecto en PS 5.1
if (-not $Source) {
    $Source = if (Test-Path (Join-Path $here 'addin')) { Join-Path $here 'addin' } else { Join-Path $here 'dist\addin' }
}

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Find-Node {
    $c = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("$env:ProgramFiles\nodejs\node.exe", "$env:LOCALAPPDATA\Programs\nodejs\node.exe")) {
        if (Test-Path $p) { return $p }
    }
}
function Winget-Install($id) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Falta winget (App Installer). Instala $id a mano y vuelve a ejecutar."
    }
    winget install --id $id --source winget -e --silent --accept-package-agreements --accept-source-agreements
    # -1978335189 = ya instalado, sin actualizacion
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) { throw "winget fallo instalando $id (codigo $LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $Source 'GlbExport\src\build.cjs'))) {
    throw "No encuentro el add-in compilado en $Source. Ejecuta .\build.ps1 primero."
}

Step 'Node.js'
$node = Find-Node
if ($node) {
    $v = [version]((& $node -v).TrimStart('v'))
    if ($v.Major -lt 18) { $node = $null; Write-Host "Node $v es muy antiguo (se necesita 18+)." }
}
if (-not $node) { Winget-Install 'OpenJS.NodeJS.LTS'; $node = Find-Node }
if (-not $node) { throw 'Node.js se instalo pero no lo encuentro; abre una consola nueva y repite.' }
Write-Host "Node: $node ($(& $node -v))"

if ($SkipNpm) { Write-Host 'Dependencias npm: omitidas (-SkipNpm)' } else {
    Step 'Dependencias (npm)'
    $deps = Join-Path $env:LOCALAPPDATA 'ArchiGlb'
    New-Item -ItemType Directory -Force $deps | Out-Null
    Copy-Item (Join-Path $Source 'GlbExport\package.json'), (Join-Path $Source 'GlbExport\package-lock.json') $deps -Force
    $npm = Join-Path (Split-Path $node) 'npm.cmd'
    $env:NPM_CONFIG_UPDATE_NOTIFIER = 'false'
    Push-Location $deps
    $ErrorActionPreference = 'Continue'   # npm escribe avisos en stderr
    try { & $npm ci --omit=dev --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci fallo' } }
    finally { Pop-Location; $ErrorActionPreference = 'Stop' }
}

if ($PyRevitDir) {
    Step "Extension de pyRevit en $PyRevitDir"
    $tpl = Join-Path $here 'pyrevit\GLB Export.pushbutton'
    if (-not (Test-Path $tpl)) { throw "No encuentro la plantilla del boton en $tpl" }
    $ext = Join-Path $PyRevitDir 'ArchiGlb.extension'
    if (Test-Path $ext) { Remove-Item -LiteralPath $ext -Recurse -Force }
    $bin = Join-Path $ext 'bin'
    New-Item -ItemType Directory -Force (Join-Path $bin 'GlbExport') | Out-Null
    Copy-Item (Join-Path $Source 'GlbExport\*') (Join-Path $bin 'GlbExport') -Recurse -Force
    $years = @()
    foreach ($y in 2023..2027) {
        $dll = Join-Path $Source "$y\GlbExport.dll"
        if (Test-Path $dll) { Copy-Item $dll (Join-Path $bin "GlbExport_$y.dll") -Force; $years += $y }
    }
    if (-not $years) { throw "No hay GlbExport.dll compilados en $Source." }
    $btn = Join-Path $ext "$Tab.tab\$Panel.panel\GLB Export.pushbutton"
    New-Item -ItemType Directory -Force $btn | Out-Null
    Copy-Item (Join-Path $tpl '*') $btn -Force
    $json = @'
{
    "name": "ArchiGlb",
    "description": "GLB Export (add-in compilado) como boton de pyRevit, dentro de una pestana existente.",
    "author": "Archiplan-ER",
    "version": "1.0.0",
    "builtin": "False",
    "default_enabled": "True",
    "rocket_mode_compatible": "True"
}
'@
    [IO.File]::WriteAllText((Join-Path $ext 'extension.json'), $json, (New-Object Text.UTF8Encoding $false))
    Write-Host ("Extension lista para Revit: " + ($years -join ', '))
    $registered = $years | Where-Object { Test-Path "$env:APPDATA\Autodesk\Revit\Addins\$_\GLBExport.addin" }
    if ($registered) { Write-Warning 'El add-in tambien esta registrado (.addin): veras ademas la pestana GLB. uninstall.ps1 sin -PyRevitDir lo quita.' }
    Write-Host "`nListo. Si falta, registra la carpeta en pyRevit (pyrevit extensions paths add `"$PyRevitDir`") y recarga pyRevit: veras GLB Export en $Tab > $Panel." -ForegroundColor Green
    return
}

Step 'Add-in de Revit'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GLBExport'
if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $Source '*') $dest -Recurse -Force

$template = @'
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>GLB Export</Name>
    <Assembly>{0}</Assembly>
    <AddInId>6b0d4c1e-3f5a-4e0b-9c57-7a1d2e8f4b10</AddInId>
    <FullClassName>GlbExport.App</FullClassName>
    <VendorId>ARCH</VendorId>
    <VendorDescription>Archiplan</VendorDescription>
  </AddIn>
</RevitAddIns>
'@
$installed = @()
foreach ($y in 2023..2027) {
    $dll = Join-Path $dest "$y\GlbExport.dll"
    if (-not (Test-Path $dll)) { continue }
    $hasRevit = (Test-Path "$env:ProgramFiles\Autodesk\Revit $y") -or (Test-Path "$env:APPDATA\Autodesk\Revit\Addins\$y")
    if (-not $hasRevit) { continue }
    $folder = "$env:APPDATA\Autodesk\Revit\Addins\$y"
    New-Item -ItemType Directory -Force $folder | Out-Null
    [IO.File]::WriteAllText("$folder\GLBExport.addin", ($template -f $dll), (New-Object Text.UTF8Encoding $false))
    $installed += $y
}
if (-not $installed) { Write-Warning 'No encontre ninguna version de Revit 2023-2027 instalada.' }
else { Write-Host ("Registrado para Revit: " + ($installed -join ', ')) }

Write-Host "`nListo. Reinicia Revit: veras la pestana GLB > GLB Export." -ForegroundColor Green
Write-Host 'Si Windows tiene Smart App Control activo, el add-in debe ir firmado (ver README).'
