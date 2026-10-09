[CmdletBinding()]
param(
  [string]$GamePath = $env:TS2Path,
  [ValidateRange(1, 3)]
  [int]$ClientCount = 1,
  [string[]]$PlayerNames,
  [ValidateRange(1024, 65535)]
  [int]$Port = 27960,
  [string]$InstanceRoot = (Join-Path $env:LOCALAPPDATA ('StolenPlayer\LocalInstances\Run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
  [switch]$SkipDeploy,
  [switch]$StageOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$deployScript = Join-Path $PSScriptRoot 'deploy.ps1'

if ([string]::IsNullOrWhiteSpace($GamePath)) {
  throw 'Pass -GamePath or set TS2Path to the Thief Simulator 2 installation.'
}
$GamePath = [System.IO.Path]::GetFullPath($GamePath)
$InstanceRoot = [System.IO.Path]::GetFullPath($InstanceRoot)
$executable = Join-Path $GamePath 'Thief Simulator 2.exe'
$pluginConfigPath = Join-Path $GamePath 'BepInEx\config\dev.viruzunit.stolenplayer.cfg'
$requiredPaths = @(
  $executable,
  (Join-Path $GamePath 'Thief Simulator 2_Data'),
  (Join-Path $GamePath 'MonoBleedingEdge'),
  (Join-Path $GamePath 'BepInEx\core'),
  (Join-Path $GamePath 'BepInEx\plugins'),
  $pluginConfigPath
)
foreach ($requiredPath in $requiredPaths) {
  if (-not (Test-Path -LiteralPath $requiredPath)) {
    throw "Required game or BepInEx path is missing: $requiredPath"
  }
}

$playerTotal = $ClientCount + 1
if (-not $PlayerNames -or $PlayerNames.Count -eq 0) {
  $PlayerNames = @('Host') + (1..$ClientCount | ForEach-Object { "Client$_" })
}
if ($PlayerNames.Count -ne $playerTotal) {
  throw "Supply exactly $playerTotal names: host first, then each client in launch order."
}
$normalizedNames = @()
foreach ($name in $PlayerNames) {
  $trimmed = ([string]$name).Trim()
  if ($trimmed.Length -lt 1 -or $trimmed.Length -gt 24 -or $trimmed -match '[\x00-\x1F]') {
    throw "Player names must contain 1 to 24 non-control characters: '$name'."
  }
  $normalizedNames += $trimmed
}
if (@($normalizedNames | Select-Object -Unique).Count -ne $playerTotal) {
  throw 'Player names must be unique, ignoring case.'
}

if (-not $SkipDeploy) {
  Write-Host 'Building and deploying the plugin to the source game installation...'
  & $deployScript -GamePath $GamePath
  if ($LASTEXITCODE -ne 0) {
    throw "Plugin deployment failed with exit code $LASTEXITCODE. Close the game and retry."
  }
}

if (Test-Path -LiteralPath $InstanceRoot) {
  throw "InstanceRoot already exists; choose a fresh path so staging cannot overwrite another run: $InstanceRoot"
}
New-Item -ItemType Directory -Path $InstanceRoot | Out-Null
$resolvedRoot = (Resolve-Path -LiteralPath $InstanceRoot).Path
if ($resolvedRoot -eq $GamePath -or $resolvedRoot.StartsWith($GamePath.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
  throw 'InstanceRoot must be outside the original game installation.'
}

function Set-ConfigValue {
  param(
    [string]$Text,
    [string]$Section,
    [string]$Key,
    [string]$Value
  )

  $sectionHeader = "[$Section]"
  $sectionPattern = '(?ms)^' + [regex]::Escape($sectionHeader) + '\s*\r?\n(?<body>.*?)(?=^\[[^\r\n]+\]\s*$|\z)'
  $match = [regex]::Match($Text, $sectionPattern)
  if (-not $match.Success) {
    return $Text.TrimEnd() + "`r`n`r`n$sectionHeader`r`n$Key = $Value`r`n"
  }

  $body = $match.Groups['body'].Value
  $keyPattern = '(?m)^\s*' + [regex]::Escape($Key) + '\s*=.*$'
  if ([regex]::IsMatch($body, $keyPattern)) {
    $replacement = [System.Text.RegularExpressions.MatchEvaluator]{ param($match) [string]::Concat($Key, ' = ', $Value) }
    $body = [regex]::Replace($body, $keyPattern, $replacement)
  }
  else {
    $body = $body.TrimEnd() + "`r`n$Key = $Value`r`n"
  }
  return $Text.Substring(0, $match.Index) + $sectionHeader + "`r`n" + $body + $Text.Substring($match.Index + $match.Length)
}

function Ensure-Junction {
  param([string]$Path, [string]$Target)
  if (Test-Path -LiteralPath $Path) {
    throw "Unexpected path already exists where a junction should be created: $Path"
  }
  New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
}

$processes = @()
for ($index = 0; $index -lt $playerTotal; $index++) {
  $role = if ($index -eq 0) { 'Host' } else { 'Client' }
  $instanceName = if ($index -eq 0) { 'Host' } else { "Client$index" }
  $instancePath = Join-Path $resolvedRoot $instanceName
  New-Item -ItemType Directory -Path $instancePath -Force | Out-Null

  Get-ChildItem -LiteralPath $GamePath -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $instancePath $_.Name) -Force
  }
  Get-ChildItem -LiteralPath $GamePath -Directory | Where-Object { $_.Name -ne 'BepInEx' } | ForEach-Object {
    Ensure-Junction -Path (Join-Path $instancePath $_.Name) -Target $_.FullName
  }

  $bepInExPath = Join-Path $instancePath 'BepInEx'
  $configPath = Join-Path $bepInExPath 'config'
  New-Item -ItemType Directory -Path $configPath -Force | Out-Null
  foreach ($directoryName in @('core', 'plugins', 'patchers')) {
    $sourceDirectory = Join-Path $GamePath "BepInEx\$directoryName"
    if (Test-Path -LiteralPath $sourceDirectory) {
      Ensure-Junction -Path (Join-Path $bepInExPath $directoryName) -Target $sourceDirectory
    }
  }
  New-Item -ItemType Directory -Path (Join-Path $bepInExPath 'cache') -Force | Out-Null
  Get-ChildItem -LiteralPath (Join-Path $GamePath 'BepInEx\config') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $configPath $_.Name) -Force
  }

  $instanceConfig = Join-Path $configPath 'dev.viruzunit.stolenplayer.cfg'
  $text = [System.IO.File]::ReadAllText($instanceConfig)
  $text = Set-ConfigValue -Text $text -Section 'Player' -Key 'Name' -Value $normalizedNames[$index]
  $text = Set-ConfigValue -Text $text -Section 'LocalTest' -Key 'StartupRole' -Value $role
  $text = Set-ConfigValue -Text $text -Section 'LocalTest' -Key 'StartupAddress' -Value '127.0.0.1'
  $text = Set-ConfigValue -Text $text -Section 'LocalTest' -Key 'StartupPort' -Value ([string]$Port)
  [System.IO.File]::WriteAllText($instanceConfig, $text, [System.Text.UTF8Encoding]::new($false))

  Write-Host "Staged $role '$($normalizedNames[$index])' at $instancePath"
}

if ($StageOnly) {
  Write-Host "Staging complete. Instance configs are under $resolvedRoot; no game processes were started."
  return
}

for ($index = 0; $index -lt $playerTotal; $index++) {
  $instanceName = if ($index -eq 0) { 'Host' } else { "Client$index" }
  $instancePath = Join-Path $resolvedRoot $instanceName
  $instanceExe = Join-Path $instancePath 'Thief Simulator 2.exe'
  $arguments = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
  $process = Start-Process -FilePath $instanceExe -WorkingDirectory $instancePath -ArgumentList $arguments -PassThru
  $processes += $process
  Write-Host "Started $instanceName ($($normalizedNames[$index])) PID=$($process.Id)"
  if ($index -eq 0 -and $ClientCount -gt 0) {
    Start-Sleep -Seconds 3
  }
}

Write-Host 'All local instances started. TS2 game saves still use the current Windows user profile and are shared between these processes.'
Write-Host 'Use this harness for connection and movement checks. Avoid save, inventory, or progression testing until game-save isolation is implemented.'
