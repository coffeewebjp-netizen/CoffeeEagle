param(
    [string]$SigningDir,
    [string]$KeytoolPath,
    [string]$Alias = "coffeeeagle-reader-release",
    [string]$DName = "CN=CoffeeEagle Reader, O=CoffeeEagle, C=JP",
    [int]$ValidityDays = 10000,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($SigningDir)) {
    $SigningDir = Join-Path $repoRoot ".tools\android-signing"
}

if ([string]::IsNullOrWhiteSpace($KeytoolPath)) {
    $keytoolCommand = Get-Command keytool.exe -ErrorAction SilentlyContinue
    if ($keytoolCommand) {
        $KeytoolPath = $keytoolCommand.Source
    }
}

if ([string]::IsNullOrWhiteSpace($KeytoolPath)) {
    $candidate = Join-Path $env:USERPROFILE ".antigravity\extensions\redhat.java-1.54.0-win32-x64\jre\21.0.10-win32-x86_64\bin\keytool.exe"
    if (Test-Path -LiteralPath $candidate) {
        $KeytoolPath = $candidate
    }
}

if ([string]::IsNullOrWhiteSpace($KeytoolPath) -or -not (Test-Path -LiteralPath $KeytoolPath)) {
    throw "keytool.exe was not found. Pass -KeytoolPath or install a JDK."
}

New-Item -ItemType Directory -Force -Path $SigningDir | Out-Null
$SigningDir = (Resolve-Path $SigningDir).Path
$keystorePath = Join-Path $SigningDir "coffeeeagle-reader-release.jks"
$propsPath = Join-Path $SigningDir "CoffeeEagle.Reader.Signing.props"

if ((Test-Path -LiteralPath $keystorePath) -and -not $Force) {
    throw "Keystore already exists: $keystorePath. Re-run with -Force only if you intentionally want a new SHA-1."
}

if ((Test-Path -LiteralPath $propsPath) -and -not $Force) {
    throw "Signing props already exists: $propsPath. Re-run with -Force only if you intentionally want a new SHA-1."
}

if ($Force) {
    Remove-Item -LiteralPath $keystorePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $propsPath -Force -ErrorAction SilentlyContinue
}

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 32
$rng.GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")

& $KeytoolPath `
    -genkeypair `
    -v `
    -keystore $keystorePath `
    -storetype PKCS12 `
    -alias $Alias `
    -keyalg RSA `
    -keysize 3072 `
    -validity $ValidityDays `
    -storepass $password `
    -keypass $password `
    -dname $DName | Out-Null

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

$props = @"
<Project>
  <PropertyGroup Condition="'`$(Configuration)' == 'Release'">
    <AndroidKeyStore>true</AndroidKeyStore>
    <AndroidSigningKeyStore>$(Escape-Xml $keystorePath)</AndroidSigningKeyStore>
    <AndroidSigningStorePass>$(Escape-Xml $password)</AndroidSigningStorePass>
    <AndroidSigningKeyAlias>$(Escape-Xml $Alias)</AndroidSigningKeyAlias>
    <AndroidSigningKeyPass>$(Escape-Xml $password)</AndroidSigningKeyPass>
  </PropertyGroup>
</Project>
"@

Set-Content -LiteralPath $propsPath -Value $props -Encoding UTF8

$certInfo = & $KeytoolPath -list -v -keystore $keystorePath -storepass $password -alias $Alias 2>&1
$sha1Line = ($certInfo | Select-String -Pattern "SHA1:" | Select-Object -First 1).Line.Trim()

Write-Host "Keystore: $keystorePath"
Write-Host "Signing props: $propsPath"
Write-Host $sha1Line
