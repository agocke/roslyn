﻿Set-Variable -Name LastExitCode 0
Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

try
{
    $SlnDir = "$PSScriptRoot/../../../"
    $BinariesPath = "$SlnDir/Binaries"

    . "$SlnDir/build/scripts/build-utils.ps1"

    # Download dotnet if it isn't already available
    Ensure-SdkInPath

    if (-not (Test-Path "$BinariesPath/CodeAnalysisRepro")) {
        $tmpFile = [System.IO.Path]::GetTempFileName()
        Invoke-WebRequest -Uri "https://roslyninfra.blob.core.windows.net/perf-artifacts/CodeAnalysisRepro.zip" -UseBasicParsing -OutFile $tmpFile
        [Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem') | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($tmpFile, $BinariesPath)
    }

    $dotnetArgs = "run -c Release CompilerPerfTests.csproj " + $args
    Exec-Command "dotnet" $dotnetArgs
}
catch {
    Write-Host $_
    Write-Host $_.Exception
    exit 1
}
