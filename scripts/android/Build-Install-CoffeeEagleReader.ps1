param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [string]$AdbPath,
    [string]$DeviceSerial,
    [switch]$Install,
    [switch]$Launch,
    [switch]$SkipBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$packageId = "net.coffeewebjp.coffeeeagle.reader"
$expectedSha1 = "15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$projectPath = Join-Path $repoRoot "src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj"
$projectBuildArgument = ".\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj"
$signingProps = Join-Path $repoRoot ".tools\android-signing\CoffeeEagle.Reader.Signing.props"

function Normalize-Sha1([string]$value) {
    return ($value -replace "[^0-9A-Fa-f]", "").ToUpperInvariant()
}

function Resolve-Directory(
    [string]$explicitPath,
    [string[]]$candidates,
    [string]$requiredRelativePath,
    [string]$displayName
) {
    $allCandidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($explicitPath)) {
        $allCandidates.Add($explicitPath)
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $allCandidates.Add($candidate)
        }
    }

    foreach ($candidate in $allCandidates) {
        try {
            $fullPath = [System.IO.Path]::GetFullPath($candidate)
        }
        catch {
            continue
        }

        if (Test-Path -LiteralPath (Join-Path $fullPath $requiredRelativePath)) {
            return $fullPath
        }
    }

    throw "$($displayName)が見つかりません。.tools配下へ復元するか明示パスを指定してください。"
}

function Get-AntigravityJavaCandidates {
    $results = [System.Collections.Generic.List[string]]::new()
    $extensionsRoot = Join-Path $env:USERPROFILE ".antigravity\extensions"
    if (-not (Test-Path -LiteralPath $extensionsRoot)) {
        return $results
    }

    foreach ($javaExtension in Get-ChildItem -Path (Join-Path $extensionsRoot "redhat.java-*") -Directory -ErrorAction SilentlyContinue) {
        $jreRoot = Join-Path $javaExtension.FullName "jre"
        foreach ($jre in Get-ChildItem -LiteralPath $jreRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending) {
            $results.Add($jre.FullName)
        }
    }

    return $results
}

function Resolve-Adb([string]$explicitPath, [string]$androidSdk) {
    $candidates = @(
        $explicitPath,
        (Join-Path $androidSdk "platform-tools\adb.exe")
    )
    $command = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "adb.exeが見つかりません。Android SDKのplatform-toolsを復元するか-AdbPathを指定してください。"
}

function Reset-ReleaseOutputs {
    $projectRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "src\CoffeeEagle.Reader"))
    $targets = @(
        [System.IO.Path]::GetFullPath((Join-Path $projectRoot "bin\Release\net10.0-android")),
        [System.IO.Path]::GetFullPath((Join-Path $projectRoot "obj\Release\net10.0-android"))
    )

    foreach ($target in $targets) {
        if (-not $target.StartsWith(
            $projectRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Release生成物以外を削除しようとしたため停止しました: $target"
        }

        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}

$androidCandidates = @(
    $env:ANDROID_SDK_ROOT,
    $env:ANDROID_HOME,
    (Join-Path $repoRoot ".tools\android-sdk"),
    (Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\android-sdk")
)
$androidSdk = Resolve-Directory $AndroidSdkDirectory $androidCandidates "platforms" "Android SDK"

$javaCandidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
    $javaCandidates.Add($env:JAVA_HOME)
}
$javaCandidates.Add((Join-Path $repoRoot ".tools\jdk\current"))
$javaCandidates.Add((Join-Path $repoRoot ".tools\jdk-17\current"))
$javaCandidates.Add((Join-Path $repoRoot ".tools\jdk-21\current"))
$javaCandidates.Add((Join-Path $repoRoot ".tools\jdk-17\jdk-17.0.19+10"))
$javaCandidates.Add((Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\jdk\current"))
$javaCandidates.Add((Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\jdk-17\current"))
$javaCandidates.Add((Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\jdk-21\current"))
$javaCandidates.Add((Join-Path $repoRoot "..\CoffeeBook\COFFEEBOOK\.tools\jdk-17\jdk-17.0.19+10"))
foreach ($candidate in Get-AntigravityJavaCandidates) {
    $javaCandidates.Add($candidate)
}
$javaSdk = Resolve-Directory $JavaSdkDirectory $javaCandidates.ToArray() "bin\java.exe" "JDK"

if ($Configuration -eq "Release" -and -not (Test-Path -LiteralPath $signingProps)) {
    throw "共通Release署名がありません。Restore-CoffeeEagleReaderSigning.ps1を先に実行してください。"
}

if ($Install -and $Configuration -ne "Release") {
    throw "既存アプリの安全な更新にはReleaseを使用してください。Debug APKはPCごとに署名が変わります。"
}

if (-not $SkipBuild) {
    Push-Location $repoRoot
    try {
        if (-not $NoRestore) {
            $restoreArguments = @(
                "restore",
                $projectBuildArgument,
                "-p:NuGetAudit=false",
                "-p:AndroidSdkDirectory=$androidSdk",
                "-p:JavaSdkDirectory=$javaSdk"
            )
            & dotnet @restoreArguments
            if ($LASTEXITCODE -ne 0) {
                throw "CoffeeEagle Readerの依存関係を復元できませんでした。"
            }
        }

        if ($Configuration -eq "Release") {
            Reset-ReleaseOutputs
            Start-Sleep -Seconds 2
        }

        $buildHelper = Join-Path $PSScriptRoot "Invoke-CoffeeEagleReaderDotnetBuild.ps1"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildHelper -RepositoryRoot $repoRoot -ProjectPath $projectBuildArgument -Configuration $Configuration -AndroidSdkDirectory $androidSdk -JavaSdkDirectory $javaSdk
        if ($LASTEXITCODE -ne 0) {
            throw "CoffeeEagle Readerの$($Configuration)ビルドに失敗しました。"
        }
    }
    finally {
        Pop-Location
    }
}

$outputDirectory = Join-Path $repoRoot "src\CoffeeEagle.Reader\bin\$Configuration\net10.0-android"
$apkPath = Join-Path $outputDirectory "$packageId-Signed.apk"
if (-not (Test-Path -LiteralPath $apkPath)) {
    throw "署名済みAPKが見つかりません: $apkPath"
}

$keytool = Join-Path $javaSdk "bin\keytool.exe"
$certInfo = & $keytool -printcert -jarfile $apkPath 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "APK署名を検証できませんでした。"
}

$sha1Line = ($certInfo | Select-String -Pattern "SHA1:" | Select-Object -First 1).Line
if ([string]::IsNullOrWhiteSpace($sha1Line)) {
    throw "APK署名のSHA-1を取得できませんでした。"
}

$actualSha1 = Normalize-Sha1 (($sha1Line -split "SHA1:", 2)[1])
if ($Configuration -eq "Release" -and $actualSha1 -ne (Normalize-Sha1 $expectedSha1)) {
    throw "APKの署名がCoffeeEagle正本と一致しません。期待SHA-1: $expectedSha1"
}

Write-Host "APK: $apkPath"
Write-Host "SHA-1: $expectedSha1"

if ($Install -or $Launch) {
    $adb = Resolve-Adb $AdbPath $androidSdk
    $deviceOutput = & $adb devices
    $devices = @(
        foreach ($line in $deviceOutput) {
            if ($line -match "^(\S+)\s+device(?:\s|$)") {
                $Matches[1]
            }
        }
    )

    if ([string]::IsNullOrWhiteSpace($DeviceSerial)) {
        if ($devices.Count -eq 0) {
            throw "接続済みAndroid端末がありません。"
        }

        if ($devices.Count -gt 1) {
            throw "複数のadb接続があります。-DeviceSerialで対象を指定してください: $($devices -join ', ')"
        }

        $DeviceSerial = $devices[0]
    }
    elseif ($DeviceSerial -notin $devices) {
        throw "指定端末がadbのdevice状態ではありません: $DeviceSerial"
    }

    if ($Install) {
        [xml]$project = Get-Content -LiteralPath $projectPath
        $versionGroup = @($project.Project.PropertyGroup) |
            Where-Object { $_.ApplicationVersion } |
            Select-Object -First 1
        $newVersionCode = [int]$versionGroup.ApplicationVersion
        $packageDump = & $adb -s $DeviceSerial shell dumpsys package $packageId
        $installedVersionLine = $packageDump | Select-String -Pattern "versionCode=(\d+)" | Select-Object -First 1
        if ($installedVersionLine -and $installedVersionLine.Matches[0].Groups[1].Value) {
            $installedVersionCode = [int]$installedVersionLine.Matches[0].Groups[1].Value
            if ($newVersionCode -le $installedVersionCode) {
                Write-Warning "APK versionCode ($newVersionCode) は端末 ($installedVersionCode) 以下です。正式更新ではApplicationVersionを増やしてください。"
            }
        }

        $installOutput = & $adb -s $DeviceSerial install -r -d $apkPath 2>&1
        $installOutput | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw "上書きインストールに失敗しました。署名不一致でもアンインストールせず、共通鍵を確認してください。"
        }
    }

    if ($Launch) {
        & $adb -s $DeviceSerial shell monkey -p $packageId -c android.intent.category.LAUNCHER 1 | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "CoffeeEagle Readerを起動できませんでした。"
        }
    }

    $updatedPackage = & $adb -s $DeviceSerial shell dumpsys package $packageId
    $updatedPackage |
        Select-String -Pattern "versionCode=|versionName=|lastUpdateTime=" |
        Select-Object -First 3 |
        ForEach-Object { Write-Host $_.Line.Trim() }
}
