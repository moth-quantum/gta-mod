# Q-DAY box bootstrap for a Shadow PC (Windows 11). Run in an elevated PowerShell.
# Paste the whole thing. Takes ~10 minutes. Safe to re-run.
# Logins (Steam, Rockstar, Claude, the Atlas key) are done by hand afterwards.
Set-ExecutionPolicy Bypass -Scope Process -Force
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 1. Tools. Chocolatey worked on the Paperspace box; same list minus Parsec (Shadow streams itself).
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
  iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
}
choco install -y googlechrome steam git python 7zip vcredist-all directx

# 2. Claude Code (native Windows installer).
irm https://claude.ai/install.ps1 | iex

# 3. Folders and the bridge path the mod and sidecar share.
New-Item -ItemType Directory -Force C:\gta-mod\bridge | Out-Null
[Environment]::SetEnvironmentVariable('QDAY_BRIDGE', 'C:\gta-mod\bridge', 'User')

# 4. Loader downloads, used later by log\install_loaders.ps1.
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'
Invoke-WebRequest 'http://www.dev-c.com/files/ScriptHookV_3889.0_1158.13.zip' -OutFile C:\gta-mod\shv.zip `
  -UserAgent $ua -Headers @{ Referer = 'http://www.dev-c.com/gtav/scripthookv/' }
Invoke-WebRequest 'https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases/download/v3.7.0-nightly.194/ScriptHookVDotNet-v3.7.0-nightly.194.zip' -OutFile C:\gta-mod\shvdn.zip -UserAgent $ua
Get-ChildItem C:\gta-mod\*.zip | ForEach-Object { Write-Host ("{0}  {1}" -f $_.Name, $_.Length) }

# 5. Read-only deploy key for the private repo. Copy the printed public key back to the Mac.
$ssh = "$env:USERPROFILE\.ssh"
New-Item -ItemType Directory -Force $ssh | Out-Null
if (-not (Test-Path "$ssh\qday_box")) { ssh-keygen -t ed25519 -N '""' -C 'qday-shadow' -f "$ssh\qday_box" | Out-Null }
[Environment]::SetEnvironmentVariable('GIT_SSH_COMMAND', "ssh -i $($ssh -replace '\\','/')/qday_box -o StrictHostKeyChecking=accept-new", 'User')
Write-Host ""
Write-Host "PUBLIC KEY (copy this to the Mac):"
Get-Content "$ssh\qday_box.pub"
Write-Host ""
Write-Host "After the key is added on GitHub, open a NEW PowerShell and run:"
Write-Host "  git clone git@github.com:moth-quantum/gta-mod.git C:\gta-mod\src"
Write-Host "Then set the Atlas key: [Environment]::SetEnvironmentVariable('MOTH_API_TOKEN', '<key>', 'User')"
