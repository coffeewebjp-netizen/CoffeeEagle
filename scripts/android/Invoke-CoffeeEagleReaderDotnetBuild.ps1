param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration,
    [Parameter(Mandatory = $true)]
    [string]$AndroidSdkDirectory,
    [Parameter(Mandatory = $true)]
    [string]$JavaSdkDirectory
)

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $RepositoryRoot

function Quote-CmdArgument([string]$value) {
    if ($value.Contains('"')) {
        throw "ビルドパスにダブルクォートは使用できません。"
    }

    return '"' + $value + '"'
}

$commandLine = @(
    "dotnet build",
    (Quote-CmdArgument $ProjectPath),
    "-c", (Quote-CmdArgument $Configuration),
    "-f net10.0-android",
    "--no-restore",
    "-nr:false",
    "-p:NuGetAudit=false",
    "-p:UseSharedCompilation=false",
    (Quote-CmdArgument "-p:AndroidSdkDirectory=$AndroidSdkDirectory"),
    (Quote-CmdArgument "-p:JavaSdkDirectory=$JavaSdkDirectory")
) -join " "

& cmd.exe /d /s /c $commandLine

exit $LASTEXITCODE
