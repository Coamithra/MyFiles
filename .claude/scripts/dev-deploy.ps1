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
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\scripts\dev-deploy.ps1 -NoBuild -Hidden
#>
param(
	[ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
	[ValidateSet('x64', 'arm64')] [string] $Platform = 'x64',
	[string] $Target = 'Files.slnx',
	[switch] $NoBuild,
	[switch] $NoLaunch,
	# Launch with the window cloaked, for driving the app through .claude\scripts\files_auto.py.
	[switch] $Hidden
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$appProject = Join-Path $repo 'src\Files.App'

if (-not $NoBuild) {
	$installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
	# Enter-VsDevShell calls vswhere itself and expects it on PATH.
	$env:PATH += ";$installer"
	$vs = & "$installer\vswhere.exe" -version '[18.0,19.0)' -latest -format json | ConvertFrom-Json | Select-Object -First 1
	if (-not $vs) { throw 'Visual Studio 2026 (18.x) not found.' }
	Import-Module (Join-Path $vs.installationPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
	Enter-VsDevShell $vs.instanceId -SkipAutomaticLocation -DevCmdArguments "-arch=$Platform -host_arch=x64" | Out-Null

	# RestorePackagesConfig covers the C++ Launcher project, which still uses packages.config.
	msbuild -restore (Join-Path $repo $Target) "-p:Configuration=$Configuration" "-p:Platform=$Platform" `
		-p:RestorePackagesConfig=true -v:quiet -clp:ErrorsOnly -nologo
	if ($LASTEXITCODE -ne 0) { throw "msbuild failed with exit code $LASTEXITCODE" }
}

$recipe = Get-ChildItem (Join-Path $appProject "bin\$Platform\$Configuration") -Recurse -Filter 'Files.App.build.appxrecipe' |
	Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $recipe) { throw 'No Files.App.build.appxrecipe found; build first.' }

[xml] $xml = Get-Content -LiteralPath $recipe.FullName -Raw
$layout = @($xml.Project.PropertyGroup.LayoutDir | Where-Object { $_ })[0]
if (-not $layout) { $layout = Join-Path $recipe.DirectoryName 'AppX' }
$items = @($xml.Project.ItemGroup.AppxPackagedFile) + @($xml.Project.ItemGroup.AppXManifest) | Where-Object { $_ }
$dependencies = @($xml.Project.ItemGroup.ResolvedSDKReference | Where-Object { $_ -and $_.Architecture -eq $Platform } |
	ForEach-Object { $_.AppxLocation } | Select-Object -Unique)

# Running binaries (Files.exe, the out-of-proc Files.App.Server.exe) lock their files in the layout.
$running = @(Get-Process | Where-Object { $_.Path -and $_.Path -like "$layout\*" })
if ($running) {
	$running | Stop-Process -Force
	$running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

$copied = 0
foreach ($item in $items) {
	$source = Get-Item -LiteralPath $item.Include
	$dest = Join-Path $layout $item.PackagePath
	$existing = Get-Item -LiteralPath $dest -ErrorAction SilentlyContinue
	if ($existing -and $existing.Length -eq $source.Length -and $existing.LastWriteTimeUtc -eq $source.LastWriteTimeUtc) { continue }
	New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
	# A just-killed process can hold its image files for a moment.
	for ($try = 1; ; $try++) {
		try { Copy-Item -LiteralPath $source.FullName -Destination $dest -Force; break }
		catch [System.IO.IOException] { if ($try -ge 20) { throw }; Start-Sleep -Milliseconds 500 }
	}
	$copied++
}
"Layout: $layout ($($items.Count) files, $copied updated)"

$manifest = Join-Path $layout 'AppxManifest.xml'
$name = ([xml] (Get-Content -LiteralPath $manifest -Raw)).Package.Identity.Name

# Registering a layout in another folder fails while the package is registered elsewhere.
$current = Get-AppxPackage -Name $name
if ($current -and $current.InstallLocation -ne $layout) {
	"Re-registering $name (was at $($current.InstallLocation)); its LocalState settings are reset."
	Remove-AppxPackage -Package $current.PackageFullName
}
# -Register only takes unpacked dependencies, so install the Windows App Runtime framework .msix separately.
# Only when missing: re-adding an installed framework fails while other apps use it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($dependency in $dependencies) {
	$zip = [IO.Compression.ZipFile]::OpenRead($dependency)
	try {
		$reader = New-Object IO.StreamReader ($zip.GetEntry('AppxManifest.xml').Open())
		$identity = ([xml] $reader.ReadToEnd()).Package.Identity
		$reader.Dispose()
	}
	finally { $zip.Dispose() }
	$installed = Get-AppxPackage -Name $identity.Name |
		Where-Object { $_.Architecture -eq $identity.ProcessorArchitecture -and [version] $_.Version -ge [version] $identity.Version }
	if (-not $installed) {
		"Installing framework $($identity.Name) $($identity.Version)"
		Add-AppxPackage -Path $dependency
	}
}
Add-AppxPackage -Register $manifest -ForceApplicationShutdown
$package = Get-AppxPackage -Name $name
"Registered: $($package.PackageFamilyName) from $($package.InstallLocation)"

if ($NoLaunch) { return }

if ($Hidden) {
	# The execution alias passes the flag through; shell:AppsFolder activation can't.
	Start-Process (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\files-dev.exe') -ArgumentList '-AutomationHidden'
	"Launched hidden."
}
else {
	Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
	"Launched."
}
