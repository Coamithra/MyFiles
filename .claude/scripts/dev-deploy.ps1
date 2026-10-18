<#
.SYNOPSIS
Builds Files.App, lays out its package the way Visual Studio's deploy does, registers it and launches it.

.DESCRIPTION
Registering the raw build output is not enough: content files such as Assets\AppTiles only land in the
package layout, and the app crashes on startup without them. This script copies every file listed in the
build's .appxrecipe into <TargetDir>\AppX and registers that layout.

The registration points at this checkout's layout folder. Run it from whichever checkout you want to run.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\scripts\dev-deploy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\scripts\dev-deploy.ps1 -NoBuild -NoLaunch
#>
param(
	[ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
	[ValidateSet('x64', 'arm64')] [string] $Platform = 'x64',
	[string] $Target = 'Files.slnx',
	[switch] $NoBuild,
	[switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$appProject = Join-Path $repo 'src\Files.App'

if (-not $NoBuild) {
	$installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
	$env:PATH += ";$installer"
	$vs = & "$installer\vswhere.exe" -version '[18.0,19.0)' -latest -property installationPath
	$instanceId = & "$installer\vswhere.exe" -version '[18.0,19.0)' -latest -property instanceId
	if (-not $vs) { throw 'Visual Studio 2026 (18.x) not found.' }
	Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
	Enter-VsDevShell $instanceId -SkipAutomaticLocation -DevCmdArguments "-arch=$Platform -host_arch=x64" | Out-Null

	# RestorePackagesConfig covers the C++ Launcher project, which still uses packages.config.
	msbuild -restore (Join-Path $repo $Target) "-p:Configuration=$Configuration" "-p:Platform=$Platform" `
		-p:RestorePackagesConfig=true -v:quiet -clp:ErrorsOnly -nologo
	if ($LASTEXITCODE -ne 0) { throw "msbuild failed with exit code $LASTEXITCODE" }
}

$recipe = Get-ChildItem (Join-Path $appProject "bin\$Platform\$Configuration") -Recurse -Filter 'Files.App.build.appxrecipe' |
	Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $recipe) { throw 'No Files.App.build.appxrecipe found; build first.' }

$layout = Join-Path $recipe.DirectoryName 'AppX'
[xml] $xml = Get-Content -LiteralPath $recipe.FullName -Raw
$items = @($xml.Project.ItemGroup.AppxPackagedFile) + @($xml.Project.ItemGroup.AppXManifest) | Where-Object { $_ }

$copied = 0
foreach ($item in $items) {
	$source = Get-Item -LiteralPath $item.Include
	$dest = Join-Path $layout $item.PackagePath
	$existing = Get-Item -LiteralPath $dest -ErrorAction SilentlyContinue
	if ($existing -and $existing.Length -eq $source.Length -and $existing.LastWriteTimeUtc -eq $source.LastWriteTimeUtc) { continue }
	New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
	Copy-Item -LiteralPath $source.FullName -Destination $dest -Force
	$copied++
}
"Layout: $layout ($($items.Count) files, $copied updated)"

$manifest = Join-Path $layout 'AppxManifest.xml'
$name = ([xml] (Get-Content -LiteralPath $manifest -Raw)).Package.Identity.Name
Get-Process Files -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$repo*" } | Stop-Process -Force

# Registering a layout in another folder fails while the package is registered elsewhere.
$current = Get-AppxPackage -Name $name
if ($current -and $current.InstallLocation -ne $layout) {
	"Re-registering $name (was at $($current.InstallLocation)); its LocalState settings are reset."
	Remove-AppxPackage -Package $current.PackageFullName
}
Add-AppxPackage -Register $manifest -ForceApplicationShutdown
$package = Get-AppxPackage -Name $name
"Registered: $($package.PackageFamilyName) from $($package.InstallLocation)"

if (-not $NoLaunch) {
	Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
	"Launched."
}
