<#
.SYNOPSIS
  Build the shareable Josour.exe, and refuse to hand over one that is not actually shareable.

.DESCRIPTION
  The product is distributed as one file that someone copies to another Windows machine and
  double-clicks. A publish that leaves anything beside the exe breaks exactly that, and breaks it
  invisibly: the copied exe starts, fails to load a WPF native library, and exits before it can
  draw a window or write a log line. Nothing appears. Nothing is logged. It just does not open.

  So this does not only build - it checks. If any file lands next to the exe, the publish is
  reported as FAILED and the folder is left in place for inspection.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1 -Runtime win-arm64 -Output publish\arm
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [string] $Output = 'publish\exe'
)

$ErrorActionPreference = 'Stop'
$client = Split-Path -Parent $PSScriptRoot | Join-Path -ChildPath 'client'
if (-not (Test-Path -LiteralPath $client)) { throw "client folder not found next to $PSScriptRoot" }
$outDir = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $client $Output }

Write-Output "publishing $Runtime -> $outDir"

# A running instance holds a lock on the exe and the publish fails halfway with an IOException
# buried under an MSB4018 stack. Say so up front instead.
$running = @(Get-Process -Name Josour -ErrorAction SilentlyContinue |
             Where-Object { $_.Path -and (Split-Path $_.Path -Parent) -eq (Resolve-Path $outDir -ErrorAction SilentlyContinue) })
if ($running.Count) {
    throw "Josour is running from $outDir (PID $($running[0].Id)). Close it, or publish to another folder with -Output."
}

Push-Location $client
try {
    & dotnet publish src\Josour.App -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -o $outDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited with $LASTEXITCODE" }
} finally {
    Pop-Location
}

$exe = Get-ChildItem -LiteralPath $outDir -File | Where-Object Name -eq 'Josour.exe'
if (-not $exe) { throw "no Josour.exe in $outDir" }

# Debug symbols for the referenced libraries land here whatever the app project says about
# DebugType, and the exe runs without them. They are not part of what anyone copies, so remove
# them rather than letting them make the folder look like it needs copying wholesale.
$symbols = @(Get-ChildItem -LiteralPath $outDir -File -Filter '*.pdb')
if ($symbols.Count) {
    $symbols | Remove-Item -Force
    Write-Output "removed $($symbols.Count) .pdb symbol file(s) - not needed to run"
}

$strays = @(Get-ChildItem -LiteralPath $outDir -File | Where-Object Name -ne 'Josour.exe')
Write-Output ''
Write-Output ("exe    : {0:N0} bytes ({1:N1} MB)" -f $exe.Length, ($exe.Length / 1MB))
Write-Output ("sha256 : " + (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash)

if ($strays.Count) {
    Write-Output ''
    Write-Output "FAILED: $($strays.Count) file(s) landed beside the exe, so the exe is NOT shareable on its own:"
    $strays | ForEach-Object { Write-Output ("  {0,-32} {1:N0} bytes" -f $_.Name, $_.Length) }
    Write-Output ''
    Write-Output 'Native WPF libraries are bundled only with IncludeNativeLibrariesForSelfExtract,'
    Write-Output 'which Josour.App.csproj sets whenever PublishSingleFile is true. If they are here,'
    Write-Output 'that property is not reaching the build - check the csproj before shipping this.'
    exit 1
}

Write-Output ''
Write-Output 'OK: one file, nothing beside it. Copy it anywhere and run it.'
