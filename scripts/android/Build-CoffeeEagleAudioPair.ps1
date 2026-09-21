param(
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$arguments = @{}
if ($AndroidSdkDirectory) { $arguments.AndroidSdkDirectory = $AndroidSdkDirectory }
if ($JavaSdkDirectory) { $arguments.JavaSdkDirectory = $JavaSdkDirectory }
if ($SkipBuild) { $arguments.SkipBuild = $true }

# Build only. Device install and launch always remain explicit separate operations.
& (Join-Path $PSScriptRoot "Build-Install-CoffeeEagleReader.ps1") -Target Reader @arguments
if ($LASTEXITCODE -ne 0) { throw "スマホAPKの作成に失敗しました。" }
& (Join-Path $PSScriptRoot "Build-Install-CoffeeEagleReader.ps1") -Target Watch @arguments
if ($LASTEXITCODE -ne 0) { throw "Watch APKの作成に失敗しました。" }

$destination = Join-Path $repoRoot "dist\watch-audio"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$packageId = "net.coffeewebjp.coffeeeagle.reader"
$phone = Join-Path $destination "CoffeeEagle-0.2.5-phone.apk"
$watch = Join-Path $destination "CoffeeEagle-0.2.1-watch.apk"
Copy-Item -LiteralPath (Join-Path $repoRoot "src\CoffeeEagle.Reader\bin\Release\net10.0-android\$packageId-Signed.apk") -Destination $phone -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "src\CoffeeEagle.Wear\bin\Release\net10.0-android\$packageId-Signed.apk") -Destination $watch -Force
$hashes = foreach ($path in @($phone, $watch)) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($path))"
}
$hashes | Set-Content -LiteralPath (Join-Path $destination "SHA256SUMS.txt") -Encoding utf8
Write-Host "配布用APK: $destination"
