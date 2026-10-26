# Builds a release of Avalon Crafting Assistance and updates the files Dalamud installs from:
#   release/latest.zip  - the plugin
#   repo.json           - the custom plugin repository friends add in /xlsettings
# Bump <Version> in AvalonPlugin/AvalonPlugin.csproj first, then run this, commit and push.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rawBase = 'https://raw.githubusercontent.com/Kynyssael/AvalonCraftingAssistance/main'

dotnet build "$root\AvalonPlugin\AvalonPlugin.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$out = "$root\AvalonPlugin\bin\Release\AvalonPlugin"
New-Item -ItemType Directory -Force "$root\release" | Out-Null
Copy-Item "$out\latest.zip" "$root\release\latest.zip" -Force

# repo.json is the manifest the build produced, plus where to download the plugin and its icon.
$manifest = Get-Content "$out\AvalonPlugin.json" -Raw | ConvertFrom-Json
$download = "$rawBase/release/latest.zip"
$manifest | Add-Member -Force NoteProperty IconUrl "$rawBase/Data/icon.png"
$manifest | Add-Member -Force NoteProperty DownloadLinkInstall $download
$manifest | Add-Member -Force NoteProperty DownloadLinkUpdate $download
$manifest | Add-Member -Force NoteProperty DownloadLinkTesting $download
$manifest | Add-Member -Force NoteProperty LastUpdate ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())

# Dalamud expects a list of plugins. Write UTF-8 without a BOM.
$json = ConvertTo-Json @($manifest) -Depth 5
[System.IO.File]::WriteAllText("$root\repo.json", $json, (New-Object System.Text.UTF8Encoding $false))

Write-Host "Released $($manifest.Name) $($manifest.AssemblyVersion) (Dalamud API $($manifest.DalamudApiLevel))."
