$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet build (Join-Path $root 'src\MoMBilingual\MoMBilingual.csproj') -c Release
Copy-Item (Join-Path $root 'src\MoMBilingual\bin\Release\netstandard2.1\MansionsBilingual.dll') `
          (Join-Path $root '..\BepInEx\plugins\MansionsBilingual\MansionsBilingual.dll') -Force
Write-Host 'Installed to BepInEx/plugins/MansionsBilingual/'
