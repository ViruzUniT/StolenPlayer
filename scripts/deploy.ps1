[CmdletBinding()]
param(
  [string]$GamePath = $env:TS2Path,
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'StolenPlayer.slnx'
$pluginOutput = Join-Path $repoRoot "src\StolenPlayer\bin\$Configuration\netstandard2.0\StolenPlayer.dll"
$bepInExPath = Join-Path $GamePath 'BepInEx'
$managedPath = Join-Path $GamePath 'Thief Simulator 2_Data\Managed'
$pluginDirectory = Join-Path $bepInExPath 'plugins'
$destinationPath = Join-Path $pluginDirectory 'StolenPlayer.dll'

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
  throw "Solution file was not found: $solutionPath"
}

if (-not (Test-Path -LiteralPath (Join-Path $bepInExPath 'core\BepInEx.dll') -PathType Leaf)) {
  throw "BepInEx was not found under the selected game path: $GamePath"
}

if (-not (Test-Path -LiteralPath (Join-Path $managedPath 'Assembly-CSharp.dll') -PathType Leaf)) {
  throw "Thief Simulator 2 managed assemblies were not found under: $GamePath"
}

if (-not (Test-Path -LiteralPath $pluginDirectory -PathType Container)) {
  throw "BepInEx plugin directory does not exist: $pluginDirectory"
}

$previousTs2Path = $env:TS2Path
try {
  $env:TS2Path = $GamePath
  Write-Host "Building StolenPlayer ($Configuration)..."
  & dotnet build $solutionPath --configuration $Configuration
  if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE. The installed plugin was not changed."
  }
}
finally {
  if ($null -eq $previousTs2Path) {
    Remove-Item Env:TS2Path -ErrorAction SilentlyContinue
  }
  else {
    $env:TS2Path = $previousTs2Path
  }
}

if (-not (Test-Path -LiteralPath $pluginOutput -PathType Leaf)) {
  throw "Build succeeded but the plugin output was not found: $pluginOutput"
}

$sourceHash = (Get-FileHash -LiteralPath $pluginOutput -Algorithm SHA256).Hash
$temporaryPath = Join-Path $pluginDirectory ("StolenPlayer.dll.deploying." + [Guid]::NewGuid().ToString('N'))
$backupSuffix = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$backupPath = Join-Path $pluginDirectory ("StolenPlayer.dll.backup.$backupSuffix")

try {
  Copy-Item -LiteralPath $pluginOutput -Destination $temporaryPath

  if ((Get-Item -LiteralPath $temporaryPath).Length -ne (Get-Item -LiteralPath $pluginOutput).Length) {
    throw 'Temporary deployment copy failed its size check. The installed plugin was not changed.'
  }

  if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
    [System.IO.File]::Replace($temporaryPath, $destinationPath, $backupPath)
    Write-Host "Previous plugin backed up to: $backupPath"
  }
  else {
    Move-Item -LiteralPath $temporaryPath -Destination $destinationPath
  }
}
catch {
  if (Test-Path -LiteralPath $temporaryPath) {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
  }

  throw "Could not deploy the plugin. Close Thief Simulator 2 and run this script again. Details: $($_.Exception.Message)"
}

$installedHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
if ($installedHash -ne $sourceHash) {
  throw "Deployment hash verification failed. Source: $sourceHash; installed: $installedHash"
}

Write-Host "Deployed and verified: $destinationPath"
Write-Host "SHA-256: $installedHash"
Write-Host 'Restart Thief Simulator 2 to load the updated plugin.'
