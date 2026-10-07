<#
 Instala GLB Export (pyRevit) para el usuario actual. No requiere administrador.
   - pyRevit: si falta, lo instala con winget.
   - Node.js 18+: si falta, instala la version LTS con winget.
   - Dependencias npm (fijadas por package-lock.json) en %LOCALAPPDATA%\ArchiGlb.
   - Copia la extension a %APPDATA%\pyRevit\Extensions.
 Uso: .\install.ps1   (desde el repo o desde la carpeta del instalador)
#>
[CmdletBinding()]
param([string]$Source = (Join-Path $PSScriptRoot 'GLBExport.extension'))
$ErrorActionPreference = 'Stop'

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

if (-not (Test-Path (Join-Path $Source 'GlbExport\src\build.cjs'))) { throw "No encuentro la extension en $Source" }

Step 'pyRevit'
$pyrevit = (Test-Path "$env:APPDATA\pyRevit") -or (Test-Path "$env:APPDATA\pyRevit-Master") -or
           (Test-Path "$env:ProgramFiles\pyRevit-Master") -or (Test-Path "$env:ProgramData\pyRevit")
if ($pyrevit) { Write-Host 'pyRevit ya esta instalado.' } else { Winget-Install 'pyRevit.pyRevit' }

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
Push-Location $deps
$env:NPM_CONFIG_UPDATE_NOTIFIER = 'false'
$ErrorActionPreference = 'Continue'   # npm escribe avisos en stderr
try { & $npm ci --omit=dev --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci fallo' } } finally { Pop-Location; $ErrorActionPreference = 'Stop' }

Step 'Extension de pyRevit'
$dest = Join-Path $env:APPDATA 'pyRevit\Extensions\GLBExport.extension'
if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
Copy-Item -LiteralPath $Source -Destination $dest -Recurse -Force
Write-Host "Copiada a $dest"

Write-Host "`nListo. Reinicia Revit (no uses Reload de pyRevit): veras la pestana GLB > GLB Export." -ForegroundColor Green
