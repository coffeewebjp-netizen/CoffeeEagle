param(
    [string]$SourceDir = $env:COFFEE_EAGLE_SIGNING_SOURCE,
    [string]$DestinationDir,
    [string]$KeytoolPath,
    [switch]$VerifyOnly,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$expectedSha1 = "15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($DestinationDir)) {
    $DestinationDir = Join-Path $repoRoot ".tools\android-signing"
}

function Normalize-Sha1([string]$value) {
    return ($value -replace "[^0-9A-Fa-f]", "").ToUpperInvariant()
}

function Resolve-Keytool([string]$explicitPath) {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($explicitPath)) {
        $candidates.Add($explicitPath)
    }

    $command = Get-Command keytool.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates.Add($command.Source)
    }

    $candidates.Add((Join-Path $repoRoot ".tools\jdk\current\bin\keytool.exe"))
    $candidates.Add((Join-Path $repoRoot ".tools\jdk-17\current\bin\keytool.exe"))
    $candidates.Add((Join-Path $repoRoot ".tools\jdk-21\current\bin\keytool.exe"))
    $candidates.Add((Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\jdk-17\current\bin\keytool.exe"))
    $candidates.Add((Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\jdk-21\current\bin\keytool.exe"))

    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates.Add((Join-Path $env:JAVA_HOME "bin\keytool.exe"))
    }

    $antigravityExtensions = Join-Path $env:USERPROFILE ".antigravity\extensions"
    if (Test-Path -LiteralPath $antigravityExtensions) {
        foreach ($javaExtension in Get-ChildItem -Path (Join-Path $antigravityExtensions "redhat.java-*") -Directory -ErrorAction SilentlyContinue) {
            $jreRoot = Join-Path $javaExtension.FullName "jre"
            foreach ($jre in Get-ChildItem -LiteralPath $jreRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending) {
                $candidates.Add((Join-Path $jre.FullName "bin\keytool.exe"))
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "keytool.exeが見つかりません。JDKを.tools配下へ復元するか、-KeytoolPathを指定してください。"
}

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    throw "共通署名のバックアップ場所を-SourceDirまたはCOFFEE_EAGLE_SIGNING_SOURCEで指定してください。"
}

$sourceDirPath = (Resolve-Path -LiteralPath $SourceDir).Path
$sourceKey = Join-Path $sourceDirPath "coffeeeagle-reader-release.jks"
$sourceProps = Join-Path $sourceDirPath "CoffeeEagle.Reader.Signing.props"
if (-not (Test-Path -LiteralPath $sourceKey) -or -not (Test-Path -LiteralPath $sourceProps)) {
    throw "署名バックアップに必要な2ファイルが揃っていません: $sourceDirPath"
}

[xml]$signing = Get-Content -LiteralPath $sourceProps
$propertyGroup = @($signing.Project.PropertyGroup) |
    Where-Object { $_.AndroidSigningStorePass -and $_.AndroidSigningKeyAlias } |
    Select-Object -First 1
if ($null -eq $propertyGroup) {
    throw "署名propsからStorePassとKeyAliasを読み取れませんでした。"
}

$storePass = [string]$propertyGroup.AndroidSigningStorePass
$keyAlias = [string]$propertyGroup.AndroidSigningKeyAlias
$resolvedKeytool = Resolve-Keytool $KeytoolPath
$certInfo = & $resolvedKeytool -list -v -keystore $sourceKey -storepass $storePass -alias $keyAlias 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "署名バックアップを検証できませんでした。"
}

$sha1Line = ($certInfo | Select-String -Pattern "SHA1:" | Select-Object -First 1).Line
if ([string]::IsNullOrWhiteSpace($sha1Line)) {
    throw "署名バックアップのSHA-1を取得できませんでした。"
}

$actualSha1 = Normalize-Sha1 (($sha1Line -split "SHA1:", 2)[1])
if ($actualSha1 -ne (Normalize-Sha1 $expectedSha1)) {
    throw "CoffeeEagleの共通署名ではありません。期待SHA-1: $expectedSha1"
}

if ($VerifyOnly) {
    Write-Host "CoffeeEagle共通署名を検証しました。"
    Write-Host "SHA-1: $expectedSha1"
    return
}

$destinationPath = [System.IO.Path]::GetFullPath($DestinationDir)
$destinationKey = Join-Path $destinationPath "coffeeeagle-reader-release.jks"
$destinationProps = Join-Path $destinationPath "CoffeeEagle.Reader.Signing.props"
if (-not $Force -and ((Test-Path -LiteralPath $destinationKey) -or (Test-Path -LiteralPath $destinationProps))) {
    throw "復元先に署名ファイルがあります。置換する場合だけ-Forceを指定してください: $destinationPath"
}

New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
Copy-Item -LiteralPath $sourceKey -Destination $destinationKey -Force
$propertyGroup.AndroidSigningKeyStore.InnerText = $destinationKey
$signing.Save($destinationProps)

Write-Host "CoffeeEagle共通署名を復元しました。"
Write-Host "SHA-1: $expectedSha1"
Write-Host "Destination: $destinationPath"
