[CmdletBinding()]
param(
    [string]$BaseRepository = "ed0ard/CS2-Bot-Improver",
    [string]$BaseTag = "v1.4.4",
    [string]$PackageVersion = "manual",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RunnerTemp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$WorkDirectory = Join-Path $RunnerTemp "cs2-bot-improver-windows"
$BaseArchive = Join-Path $WorkDirectory "CS2BotImprover-$BaseTag.zip"
$BaseDirectory = Join-Path $WorkDirectory "base"
$VpkArchive = Join-Path $WorkDirectory "VPKEdit.zip"
$VpkDirectory = Join-Path $WorkDirectory "vpkedit"
$ProfileDirectory = Join-Path $WorkDirectory "profiles"
$VpkCli = Join-Path $VpkDirectory "vpkeditcli.exe"

$VpkEditVersion = "v5.0.0.4"
$VpkEditUri = "https://github.com/craftablescience/VPKEdit/releases/download/$VpkEditVersion/VPKEdit-Windows-Standalone-msvc-Release.zip"
$VpkEditSha256 = "d9ceaf3f16aea17c06e3be79da93c55a597af1487eed8a1b42dada1ea8d54503"
$CreatedCompileFiles = [System.Collections.Generic.List[string]]::new()
$CompileBackups = [System.Collections.Generic.List[object]]::new()
$ExpectedBotControllerAbi = 17

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $Root "artifacts"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Require-File {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
}

function Copy-IfPresent {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    if (Test-Path -LiteralPath $Source -PathType Leaf) {
        Copy-Item -LiteralPath $Source -Destination $DestinationDirectory -Force
    }
}

function Assert-BotControllerAbi {
    param([Parameter(Mandatory = $true)][string]$NativeLibraryPath)

    $probeSource = @"
using System;
using System.Runtime.InteropServices;

public static class BotControllerAbiProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVersionDelegate();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? path);

    public static int Read(string libraryPath)
    {
        string? libraryDirectory = Path.GetDirectoryName(libraryPath);
        if (string.IsNullOrWhiteSpace(libraryDirectory) || !SetDllDirectory(libraryDirectory))
            throw new InvalidOperationException($"Unable to set native DLL search directory: {libraryDirectory}");

        IntPtr library = IntPtr.Zero;
        try
        {
            library = NativeLibrary.Load(libraryPath);
            IntPtr export = NativeLibrary.GetExport(library, "BotController_GetVersion");
            var getVersion = Marshal.GetDelegateForFunctionPointer<GetVersionDelegate>(export);
            return getVersion();
        }
        finally
        {
            if (library != IntPtr.Zero)
                NativeLibrary.Free(library);
            SetDllDirectory(null);
        }
    }
}
"@
    Add-Type -TypeDefinition $probeSource -Language CSharp -ErrorAction Stop | Out-Null
    $actualAbi = [BotControllerAbiProbe]::Read($NativeLibraryPath)
    if ($actualAbi -ne $ExpectedBotControllerAbi) {
        throw "BotController ABI mismatch. Expected $ExpectedBotControllerAbi, got $actualAbi from $NativeLibraryPath"
    }
    Write-Host "BotController native ABI: $actualAbi"
    return $actualAbi
}

try {
    if (Test-Path -LiteralPath $WorkDirectory) {
        Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $WorkDirectory, $BaseDirectory, $VpkDirectory, $ProfileDirectory -Force | Out-Null

    if (Test-Path -LiteralPath $OutputDirectory) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

    $BaseUri = "https://github.com/$BaseRepository/releases/download/$BaseTag/CS2BotImprover.zip"
    Write-Host "Downloading Windows base package: $BaseUri"
    Invoke-WebRequest -Uri $BaseUri -OutFile $BaseArchive
    Expand-Archive -LiteralPath $BaseArchive -DestinationPath $BaseDirectory -Force

    Write-Host "Downloading VPKEdit CLI: $VpkEditUri"
    Invoke-WebRequest -Uri $VpkEditUri -OutFile $VpkArchive
    $VpkSha256 = (Get-FileHash -LiteralPath $VpkArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($VpkSha256 -ne $VpkEditSha256) {
        throw "VPKEdit checksum mismatch. Expected $VpkEditSha256, got $VpkSha256"
    }
    Expand-Archive -LiteralPath $VpkArchive -DestinationPath $VpkDirectory -Force
    Require-File $VpkCli

    $Staging = $BaseDirectory
    $RayTraceApi = Join-Path $Staging "addons/counterstrikesharp/shared/RayTraceApi/RayTraceApi.dll"
    $BotControllerApi = Join-Path $Staging "addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll"
    Require-File (Join-Path $Staging "addons/RayTrace/bin/win64/RayTrace.dll")
    $BotControllerNative = Join-Path $Staging "addons/BotController/bin/win64/BotController.dll"
    Require-File $BotControllerNative
    Require-File $RayTraceApi
    Require-File $BotControllerApi
    $BotControllerAbi = Assert-BotControllerAbi $BotControllerNative

    $RayTraceCompileTargets = @(
        "addons/counterstrikesharp/plugins/BotAimImprover/libs",
        "addons/counterstrikesharp/plugins/NadeSystem/libs",
        "addons/counterstrikesharp/plugins/BotState/libs"
    )
    foreach ($Target in $RayTraceCompileTargets) {
        $TargetDirectory = Join-Path $Root $Target
        New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
        $TargetFile = Join-Path $TargetDirectory "RayTraceApi.dll"
        if (Test-Path -LiteralPath $TargetFile -PathType Leaf) {
            $BackupFile = Join-Path $WorkDirectory ("compile-backups/" + $CompileBackups.Count + "/RayTraceApi.dll")
            New-Item -ItemType Directory -Path (Split-Path $BackupFile -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $TargetFile -Destination $BackupFile -Force
            $CompileBackups.Add([pscustomobject]@{ Destination = $TargetFile; Backup = $BackupFile })
        }
        else {
            $CreatedCompileFiles.Add($TargetFile)
        }
        Copy-Item -LiteralPath $RayTraceApi -Destination $TargetFile -Force
    }

    $BotControllerCompileDirectory = Join-Path $Root "addons/counterstrikesharp/plugins/BotState/libs"
    $BotControllerCompileFile = Join-Path $BotControllerCompileDirectory "BotControllerApi.dll"
    New-Item -ItemType Directory -Path $BotControllerCompileDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $BotControllerCompileFile -PathType Leaf) {
        $BackupFile = Join-Path $WorkDirectory ("compile-backups/" + $CompileBackups.Count + "/BotControllerApi.dll")
        New-Item -ItemType Directory -Path (Split-Path $BackupFile -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $BotControllerCompileFile -Destination $BackupFile -Force
        $CompileBackups.Add([pscustomobject]@{ Destination = $BotControllerCompileFile; Backup = $BackupFile })
    }
    else {
        $CreatedCompileFiles.Add($BotControllerCompileFile)
    }
    Copy-Item -LiteralPath $BotControllerApi -Destination $BotControllerCompileFile -Force

    $Projects = @(
        "addons/counterstrikesharp/plugins/BotBuy/BotBuy.csproj",
        "addons/counterstrikesharp/plugins/BotAimImprover/BotAimImprover.csproj",
        "addons/counterstrikesharp/plugins/BotState/BotState.csproj"
    )
    $TestProject = Join-Path $Root "tests/BotBehavior.Tests/BotBehavior.Tests.csproj"

    Write-Host "Restoring active projects serially"
    foreach ($Project in $Projects) {
        Invoke-Checked "dotnet" @("restore", (Join-Path $Root $Project), "--nologo", "--verbosity", "minimal")
    }
    Invoke-Checked "dotnet" @("restore", $TestProject, "--nologo", "--verbosity", "minimal")

    Write-Host "Building modified plugins serially"
    foreach ($Project in $Projects) {
        Invoke-Checked "dotnet" @(
            "build",
            (Join-Path $Root $Project),
            "--configuration", "Release",
            "--no-restore",
            "--nologo",
            "-p:ContinuousIntegrationBuild=true",
            "-p:Deterministic=true",
            "--verbosity", "minimal"
        )
    }

    Invoke-Checked "dotnet" @(
        "test",
        $TestProject,
        "--configuration", "Release",
        "--no-restore",
        "--nologo",
        "--logger", "console;verbosity=minimal"
    )

    $PluginTargets = @(
        @{ Name = "BotBuy"; Output = "addons/counterstrikesharp/plugins/BotBuy/bin/Release/net8.0" },
        @{ Name = "BotAimImprover"; Output = "addons/counterstrikesharp/plugins/BotAimImprover/bin/Release/net10.0" },
        @{ Name = "BotState"; Output = "addons/counterstrikesharp/plugins/BotState/bin/Release/net10.0" }
    )

    foreach ($Plugin in $PluginTargets) {
        $SourceDirectory = Join-Path $Root $Plugin.Output
        $DestinationDirectory = Join-Path $Staging "addons/counterstrikesharp/plugins/$($Plugin.Name)"
        Require-File (Join-Path $SourceDirectory "$($Plugin.Name).dll")
        New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

        Copy-IfPresent (Join-Path $SourceDirectory "$($Plugin.Name).dll") $DestinationDirectory
        Copy-IfPresent (Join-Path $SourceDirectory "$($Plugin.Name).deps.json") $DestinationDirectory
        Copy-IfPresent (Join-Path $SourceDirectory "$($Plugin.Name).pdb") $DestinationDirectory

        $PluginLocalRayTrace = Join-Path $DestinationDirectory "RayTraceApi.dll"
        if (Test-Path -LiteralPath $PluginLocalRayTrace) {
            Remove-Item -LiteralPath $PluginLocalRayTrace -Force
        }
    }

    $SourceGrenades = Join-Path $Root "addons/counterstrikesharp/plugins/NadeSystem/grenades"
    $DestinationGrenades = Join-Path $Staging "addons/counterstrikesharp/plugins/NadeSystem/grenades"
    if (Test-Path -LiteralPath $DestinationGrenades) {
        Remove-Item -LiteralPath $DestinationGrenades -Recurse -Force
    }
    New-Item -ItemType Directory -Path $DestinationGrenades -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceGrenades "*") -Destination $DestinationGrenades -Recurse -Force

    $DestinationConfig = Join-Path $Staging "cfg/bot_improver_profile.cfg"
    Copy-Item -LiteralPath (Join-Path $Root "cfg/bot_improver_profile.cfg") -Destination $DestinationConfig -Force

    foreach ($Profile in @("High", "Medium", "Low")) {
        $SourceProfile = Join-Path $Root "overrides/$Profile/botprofile.db"
        Require-File $SourceProfile

        $ProfileInputDirectory = Join-Path $ProfileDirectory $Profile
        New-Item -ItemType Directory -Path $ProfileInputDirectory -Force | Out-Null
        Copy-Item -LiteralPath $SourceProfile -Destination (Join-Path $ProfileInputDirectory "botprofile.db") -Force

        $ProfileOutputDirectory = Join-Path $Staging "overrides/$Profile"
        New-Item -ItemType Directory -Path $ProfileOutputDirectory -Force | Out-Null
        $ProfileVpk = Join-Path $ProfileOutputDirectory "botprofile.vpk"
        Invoke-Checked $VpkCli @(
            $ProfileInputDirectory,
            "--output", $ProfileVpk,
            "--type", "vpk",
            "--version", "2",
            "--single-file",
            "--no-progress"
        )
        Require-File $ProfileVpk

        if ($Profile -eq "Medium") {
            Copy-Item -LiteralPath $ProfileVpk -Destination (Join-Path $Staging "overrides/botprofile.vpk") -Force
        }
    }

    $Metadata = @(
        "CS2-Bot-Improver Windows package",
        "Source commit: $($env:GITHUB_SHA)",
        "Base package: $BaseRepository@$BaseTag",
        "BotController ABI: $BotControllerAbi (expected $ExpectedBotControllerAbi)",
        "Package version: $PackageVersion",
        "Built at: $([DateTime]::UtcNow.ToString('o'))"
    )
    Set-Content -LiteralPath (Join-Path $Staging "BUILD-METADATA.txt") -Value $Metadata -Encoding UTF8

    $PackagePath = Join-Path $OutputDirectory "CS2BotImprover-windows-$PackageVersion.zip"
    Compress-Archive -Path (Join-Path $Staging "*") -DestinationPath $PackagePath -CompressionLevel Optimal -Force
    $PackageHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$PackagePath.sha256" -Value "$PackageHash  $(Split-Path $PackagePath -Leaf)" -Encoding ASCII

    Write-Host "Windows package created: $PackagePath"
    Write-Host "Windows package SHA256: $PackageHash"
}
finally {
    foreach ($Backup in $CompileBackups) {
        if (Test-Path -LiteralPath $Backup.Backup -PathType Leaf) {
            Copy-Item -LiteralPath $Backup.Backup -Destination $Backup.Destination -Force
        }
    }
    foreach ($CompileFile in $CreatedCompileFiles) {
        if (Test-Path -LiteralPath $CompileFile -PathType Leaf) {
            Remove-Item -LiteralPath $CompileFile -Force
        }
    }
}
