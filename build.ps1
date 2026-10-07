<#
 Compila el add-in para Revit 2023..2027 y deja todo listo para instalar en dist\addin\
   dist\addin\<anio>\GlbExport.dll (+ dependencias)
   dist\addin\GlbExport\...          (conversor Node)
 Requiere el SDK de .NET 10 (compila 2027) en el PATH o en %LOCALAPPDATA%\Microsoft\dotnet.
 Uso: .\build.ps1 [-Years 2023,2024]
#>
param([int[]]$Years = @(2023, 2024, 2025, 2026, 2027))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path $localDotnet) { $env:PATH = "$localDotnet;$env:PATH"; $env:DOTNET_ROOT = $localDotnet }

$dist = Join-Path $root 'dist\addin'
if (Test-Path $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
foreach ($y in $Years) {
    $cfg = 'R' + ($y % 100)
    Write-Host "==> Revit $y ($cfg)" -ForegroundColor Cyan
    dotnet build (Join-Path $root 'src\GlbExport\GlbExport.csproj') -c $cfg -nologo -v q -p:TreatWarningsAsErrors=false
    if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion para Revit $y" }
}
$node = Join-Path $dist 'GlbExport'
New-Item -ItemType Directory -Force (Join-Path $node 'src') | Out-Null
Copy-Item (Join-Path $root 'GlbExport\src\*') (Join-Path $node 'src') -Recurse
Copy-Item (Join-Path $root 'GlbExport\package.json'), (Join-Path $root 'GlbExport\package-lock.json'), (Join-Path $root 'GlbExport\rules.default.json') $node
Write-Host "Listo: $dist" -ForegroundColor Green
