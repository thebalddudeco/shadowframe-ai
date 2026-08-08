param(
  [switch]$SkipCoreBuild,
  [switch]$ReuseExistingPayload,
  [switch]$PublicRelease,
  [switch]$KeepPayload,
  [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot "release"
$coreRoot = Join-Path $releaseRoot "Shadowframe-Core"
$publicCorePayloadUrl = "https://huggingface.co/datasets/TheBaldDudeCo/shadowframe-ai-public-release/resolve/main/Shadowframe-Core.tar?download=1"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $releaseRoot "Shadowframe-Installer" }
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$allowedRoot = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\')
if (!$output.StartsWith($allowedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
  throw "The installer output must stay inside $allowedRoot"
}

if (!$SkipCoreBuild) {
  $coreArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "Build-Shadowframe-Core.ps1"))
  if ($PublicRelease) { $coreArguments += @("-Profile", "public") }
  & powershell.exe @coreArguments
  if ($LASTEXITCODE -ne 0) { throw "The Core package could not be built." }
}
if (!(Test-Path -LiteralPath (Join-Path $coreRoot "Shadowframe.exe"))) {
  throw "The verified Core staging package is missing. Build Phase 1 first."
}

function New-TarPayload([string]$SourceDirectory, [string]$ArchivePath) {
  $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
  if ($tar) {
    & $tar.Source -cf $ArchivePath -C $SourceDirectory .
    if ($LASTEXITCODE -ne 0) { throw "The Core payload archive could not be created." }
    return
  }

  $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
  if ($pwsh) {
    $helper = Join-Path $env:TEMP "Shadowframe-NewTar-$([guid]::NewGuid().ToString('N')).ps1"
    try {
      @'
param(
  [string]$SourceDirectory,
  [string]$ArchivePath
)
$ErrorActionPreference = "Stop"
[System.Formats.Tar.TarFile]::CreateFromDirectory($SourceDirectory, $ArchivePath, $false)
'@ | Set-Content -LiteralPath $helper -Encoding UTF8
      & $pwsh.Source -NoProfile -File $helper -SourceDirectory $SourceDirectory -ArchivePath $ArchivePath
      if ($LASTEXITCODE -ne 0) { throw "The managed Core payload archive could not be created." }
      return
    } finally {
      Remove-Item -LiteralPath $helper -Force -ErrorAction SilentlyContinue
    }
  }

  throw "tar.exe was not found, and the managed Core payload archive could not be created."
}

function Get-Sha256([string]$Path) {
  try {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash
  } catch {
    # Windows PowerShell environments launched from shells with customized
    # module paths can occasionally fail to expose Get-FileHash. The .NET
    # fallback keeps release builds deterministic.
  }

  $stream = [IO.File]::OpenRead($Path)
  try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
      return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "")
    } finally {
      $sha.Dispose()
    }
  } finally {
    $stream.Dispose()
  }
}

$payload = Join-Path $output "Shadowframe-Core.tar"
$generatedRoot = Join-Path $PSScriptRoot "Shadowframe.Installer\Generated"
if (Test-Path -LiteralPath $output) {
  if ($ReuseExistingPayload -and (Test-Path -LiteralPath $payload)) {
    Get-ChildItem -LiteralPath $output -Force |
      Where-Object { !$_.Name.Equals("Shadowframe-Core.tar", [StringComparison]::OrdinalIgnoreCase) } |
      Remove-Item -Recurse -Force
  } else {
    Remove-Item -LiteralPath $output -Recurse -Force
  }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (Test-Path -LiteralPath $generatedRoot) { Remove-Item -LiteralPath $generatedRoot -Recurse -Force }
New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null

Write-Host "Packing the verified Shadowframe Core payload..."
if (!($ReuseExistingPayload -and (Test-Path -LiteralPath $payload))) {
  $temporaryPayload = Join-Path $output "Shadowframe-Core.tar.$([guid]::NewGuid().ToString('N')).partial"
  try {
    New-TarPayload $coreRoot $temporaryPayload
    Move-Item -LiteralPath $temporaryPayload -Destination $payload -Force
  } finally {
    Remove-Item -LiteralPath $temporaryPayload -Force -ErrorAction SilentlyContinue
  }
} else {
  Write-Host "Reusing the existing Shadowframe Core payload..."
}

$files = Get-ChildItem -LiteralPath $coreRoot -File -Recurse
$bytes = ($files | Measure-Object Length -Sum).Sum
$hash = Get-Sha256 $payload
$manifest = [ordered]@{
  version = "0.3.6"
  payloadFile = "Shadowframe-Core.tar"
  sha256 = $hash
  uncompressedBytes = $bytes
  fileCount = $files.Count
  payloadUrl = if ($PublicRelease) { $publicCorePayloadUrl } else { $null }
}
$manifestPath = Join-Path $output "Shadowframe-Package.json"
$profilePath = Join-Path $output "Shadowframe-ReleaseProfile.json"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
@{ profile = if ($PublicRelease) { "public" } else { "creator" } } | ConvertTo-Json | Set-Content -LiteralPath $profilePath -Encoding UTF8

function Copy-SamplePrompts([string]$SourceRoot, [string]$DestinationRoot, [bool]$PublicOnly) {
  if (!(Test-Path -LiteralPath $SourceRoot)) { return }
  if (Test-Path -LiteralPath $DestinationRoot) { Remove-Item -LiteralPath $DestinationRoot -Recurse -Force }
  New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

  $modelSets = @(
    @{ Source = "redcraft-prompts"; Name = "RedCraft" },
    @{ Source = "moody-prompts"; Name = "Moody Real" },
    @{ Source = "ltx-prompts"; Name = "LTX Video" }
  )
  $nsfwPattern = "(?i)\b(nude|topless|breast|nipple|pussy|labia|areola|sex|bondage|slave|see-through|transparent clothes|deepthroat|porn|erotic|explicit|genital|mons pubis)\b"
  foreach ($set in $modelSets) {
    $source = Join-Path $SourceRoot $set.Source
    if (!(Test-Path -LiteralPath $source)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Filter "*.txt") {
      if ($file.Name.Equals("README.txt", [StringComparison]::OrdinalIgnoreCase)) { continue }
      $text = Get-Content -Raw -LiteralPath $file.FullName
      $category = if ($text -match $nsfwPattern -or $file.Name -match $nsfwPattern) { "NSFW" } else { "SFW" }
      if ($PublicOnly -and $category -ne "SFW") { continue }
      $targetFolder = Join-Path (Join-Path $DestinationRoot $category) $set.Name
      New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
      Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $targetFolder $file.Name) -Force
    }
  }

  $categories = if ($PublicOnly) { @("SFW") } else { @("SFW", "NSFW") }
  foreach ($category in $categories) {
    $readme = Join-Path (Join-Path $DestinationRoot $category) "README.txt"
    New-Item -ItemType Directory -Path (Split-Path -Parent $readme) -Force | Out-Null
    @"
Shadowframe $category Sample Prompts

Open a model-set folder, copy a prompt, and paste it into Shadowframe.

Folders are separated so new users can quickly choose safer showcase prompts or adult-oriented examples.
"@ | Set-Content -LiteralPath $readme -Encoding UTF8
  }
}

function New-ZipArchive([string]$SourceDirectory, [string]$ArchivePath) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
  [System.IO.Compression.ZipFile]::CreateFromDirectory($SourceDirectory, $ArchivePath)
}

Copy-SamplePrompts (Join-Path $projectRoot "samples") (Join-Path $output "Sample Prompts") $PublicRelease
$samplePromptsPath = Join-Path $output "Sample Prompts"

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $generatedRoot "Shadowframe-Package.json") -Force
Copy-Item -LiteralPath $profilePath -Destination (Join-Path $generatedRoot "Shadowframe-ReleaseProfile.json") -Force
if (Test-Path -LiteralPath $samplePromptsPath) {
  New-ZipArchive $samplePromptsPath (Join-Path $generatedRoot "Sample-Prompts.zip")
}

Write-Host "Building the Shadowframe Setup application..."
$project = Join-Path $PSScriptRoot "Shadowframe.Installer\Shadowframe.Installer.csproj"
$publish = Join-Path $releaseRoot "installer-publish"
$installerBuildRoot = Join-Path $PSScriptRoot "Shadowframe.Installer"
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
foreach ($stalePath in @((Join-Path $installerBuildRoot "bin"), (Join-Path $installerBuildRoot "obj"))) {
  if (Test-Path -LiteralPath $stalePath) { Remove-Item -LiteralPath $stalePath -Recurse -Force }
}
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "Shadowframe Setup could not be built." }
Copy-Item -LiteralPath (Join-Path $publish "Shadowframe Setup.exe") -Destination (Join-Path $output "Shadowframe Setup.exe") -Force

$setupHash = Get-Sha256 (Join-Path $output "Shadowframe Setup.exe")
@(
  "$setupHash  Shadowframe Setup.exe"
) | Set-Content -LiteralPath (Join-Path $output "SHA256SUMS.txt") -Encoding ASCII

@"
Shadowframe AI — Windows installer

Everything required for setup is packaged inside:
  Shadowframe Setup.exe

SHA256SUMS.txt contains optional integrity checks.

Run Shadowframe Setup.exe. Public builds are designed as a one-file install: choose your Shadowframe folder, then let Setup automatically fetch the public Anima, Wan, and PhotoReal model packs from Hugging Face during setup when they are not already bundled beside the installer. Creator/private builds can still chain adjacent model-pack installers automatically.
The installer also includes $(if ($PublicRelease) { "SFW" } else { "SFW and NSFW" }) starter sample prompts and places them into the installed Shadowframe folder automatically.

Silent install:
  "Shadowframe Setup.exe" /SILENT

Custom folder:
  "Shadowframe Setup.exe" /SILENT /INSTALLDIR="D:\Apps\Shadowframe AI"

Skip automatic model packs:
  "Shadowframe Setup.exe" /SILENT /NOMODELPACKS
"@ | Set-Content -LiteralPath (Join-Path $output "README.txt") -Encoding UTF8

Write-Host "Installer payload SHA-256: $hash"

foreach ($path in @(
  $(if ($KeepPayload) { $null } else { $payload }),
  $manifestPath,
  $profilePath,
  $samplePromptsPath
)) {
  if ($path -and (Test-Path -LiteralPath $path)) {
    Remove-Item -LiteralPath $path -Recurse -Force
  }
}

Write-Host "Shadowframe installer created at: $output" -ForegroundColor Green


