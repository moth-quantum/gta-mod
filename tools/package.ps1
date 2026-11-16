# Builds Q-DAY and packages it into dist\QDay-<commit>.zip:
#   README.md
#   scripts\QDay.dll, QDay.ini, QDay.pdb
# Usage (from anywhere): powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$mod  = Join-Path $root 'mod'
$dist = Join-Path $root 'dist'

dotnet build $mod -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$ini = Join-Path $mod 'QDay.ini'

$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty  = if (git -C $root status --porcelain -- mod README.md) { '-dirty' } else { '' }
$name   = "QDay-$commit$dirty"

$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$scripts = New-Item -ItemType Directory -Force (Join-Path $stage 'scripts')

Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $mod 'bin\QDay.dll'), (Join-Path $mod 'bin\QDay.pdb'), $ini $scripts

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host "Packaged $zip"
