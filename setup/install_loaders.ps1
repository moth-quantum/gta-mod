# Waits for GTA V Legacy (Steam app 271590) to finish installing, then installs
# Script Hook V, ScriptHookVDotNet and the Q-DAY mod into the game folder.
# Safe to re-run. Logs to C:\gta-mod\bridge\install.log.
$ErrorActionPreference = 'Continue'
$steam = 'C:\Program Files (x86)\Steam\steamapps'
$acf   = "$steam\appmanifest_271590.acf"
$src   = 'C:\gta-mod\src'
$log   = 'C:\gta-mod\bridge\install.log'
function L($m) { $line = "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Write-Host $line; Add-Content $log $line }

L "waiting for Legacy to finish (StateFlags 4 in appmanifest)"
while ($true) {
  if (Test-Path $acf) {
    $state = (Select-String '"StateFlags"\s+"(\d+)"' $acf).Matches[0].Groups[1].Value
    $dl = (Select-String '"BytesDownloaded"\s+"(\d+)"' $acf).Matches[0].Groups[1].Value
    $to = (Select-String '"BytesToDownload"\s+"(\d+)"' $acf).Matches[0].Groups[1].Value
    if ($state -eq '4') { break }
    L ("state {0}, {1:N1} / {2:N1} GB" -f $state, ([double]$dl/1GB), ([double]$to/1GB))
  } else { L "no manifest yet" }
  Start-Sleep 120
}
$dir = (Select-String '"installdir"\s+"([^"]+)"' $acf).Matches[0].Groups[1].Value
$game = "$steam\common\$dir"
L "installed at $game"
if (-not (Test-Path "$game\GTA5.exe")) { L "GTA5.exe not found, stopping"; exit 1 }

# Script Hook V: only the two runtime files, not the trainer
Expand-Archive C:\gta-mod\shv.zip -DestinationPath C:\gta-mod\shv -Force
Copy-Item (Get-ChildItem C:\gta-mod\shv -Recurse -Filter ScriptHookV.dll).FullName $game -Force
Copy-Item (Get-ChildItem C:\gta-mod\shv -Recurse -Filter dinput8.dll).FullName $game -Force
L "Script Hook V copied"

# ScriptHookVDotNet 3
Expand-Archive C:\gta-mod\shvdn.zip -DestinationPath C:\gta-mod\shvdn -Force
foreach ($f in 'ScriptHookVDotNet.asi','ScriptHookVDotNet2.dll','ScriptHookVDotNet3.dll','ScriptHookVDotNet.ini') {
  $p = Get-ChildItem C:\gta-mod\shvdn -Recurse -Filter $f | Select-Object -First 1
  if ($p) { Copy-Item $p.FullName $game -Force } else { L "missing $f in shvdn.zip" }
}
L "ScriptHookVDotNet copied"

# The mod
New-Item -ItemType Directory -Force "$game\scripts" | Out-Null
Copy-Item "$src\mod\bin\QDay.dll" "$game\scripts\QDay.dll" -Force
Copy-Item "$src\mod\locations.json" "$game\scripts\QDay.locations.json" -Force
L "Q-DAY mod copied"
Get-ChildItem $game -Filter '*.dll' | Where-Object Name -match 'ScriptHook|dinput8' | ForEach-Object { L ("  {0}  {1}" -f $_.Name, $_.Length) }
Get-ChildItem "$game\scripts" | ForEach-Object { L ("  scripts\{0}  {1}" -f $_.Name, $_.Length) }
L "DONE. Launch Legacy from Steam, log into the Rockstar Launcher, story mode, press F9."
