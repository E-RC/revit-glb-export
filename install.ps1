<#
 Instala GLB Export (add-in de Revit 2023..2027) para el usuario actual. No requiere administrador.
   - Node.js 18+: si falta, instala la version LTS con winget.
   - Dependencias npm (fijadas por package-lock.json) en %LOCALAPPDATA%\ArchiGlb.
   - Copia el add-in a %LOCALAPPDATA%\Programs\GLBExport y registra GLBExport.addin
     en %APPDATA%\Autodesk\Revit\Addins\<anio> para cada Revit instalado.
 Uso: .\install.ps1 [-Source <carpeta con <anio>\GlbExport.dll y GlbExport\>]   (por defecto dist\addin)
#>
[CmdletBinding()]
param([string]$Source)
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
