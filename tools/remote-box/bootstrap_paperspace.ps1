# Q-DAY box bootstrap. Run in an elevated PowerShell on the Paperspace machine (Windows 10 / Server 2022 desktop).
# Paste the whole thing. Takes ~10 minutes. Reboot once at the end.
Set-ExecutionPolicy Bypass -Scope Process -Force
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 1. Server 2022 quirks: audio off by default (Parsec would be silent), IE hardening blocks downloads.
Set-Service -Name Audiosrv -StartupType Automatic; Start-Service Audiosrv
Set-Service -Name AudioEndpointBuilder -StartupType Automatic; Start-Service AudioEndpointBuilder
$ie = 'HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components\{A509B1A7-37EF-4b3f-8CFC-4F3A74704073}'
if (Test-Path $ie) { Set-ItemProperty $ie -Name IsInstalled -Value 0 }
$ie2 = 'HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components\{A509B1A8-37EF-4b3f-8CFC-4F3A74704073}'
if (Test-Path $ie2) { Set-ItemProperty $ie2 -Name IsInstalled -Value 0 }

# 2. Chocolatey, then everything else through it.
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
  iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
}
choco install -y googlechrome parsec steam git python nodejs-lts 7zip vcredist-all directx

# 3. Claude Code (native Windows installer).
irm https://claude.ai/install.ps1 | iex

# 4. The repo and the bridge folder.
if (-not (Test-Path C:\gta-mod)) { git clone https://github.com/moth-quantum/gta-mod C:\gta-mod }  # or copy it over Parsec if private
New-Item -ItemType Directory -Force C:\gta-mod\bridge | Out-Null
[Environment]::SetEnvironmentVariable('QDAY_BRIDGE', 'C:\gta-mod\bridge', 'User')
Write-Host ""
Write-Host "Now set the Atlas key for your user (never in a file):"
Write-Host "  [Environment]::SetEnvironmentVariable('MOTH_API_TOKEN', '<paste>', 'User')"
Write-Host "Then: install Parsec login, Steam login, buy/install GTA V Enhanced + Legacy. Reboot."
