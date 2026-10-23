[Net.ServicePointManager]::SecurityProtocol='Tls12'
Set-ExecutionPolicy Bypass -Scope Process -Force
Set-Service Audiosrv -StartupType Automatic; Start-Service Audiosrv
Set-Service AudioEndpointBuilder -StartupType Automatic; Start-Service AudioEndpointBuilder
iex ((New-Object Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
choco install -y googlechrome parsec steam git python nodejs-lts 7zip vcredist-all directx
irm https://claude.ai/install.ps1 | iex
mkdir C:\gta-mod\bridge -Force | Out-Null
[Environment]::SetEnvironmentVariable('QDAY_BRIDGE','C:\gta-mod\bridge','User')
