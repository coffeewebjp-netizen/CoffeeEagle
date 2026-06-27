# Android Release

CoffeeEagle は Android 専用アプリです。PC版は作らず、PC版 EAGLE が管理している `.library` を Android から読むビューアとして扱います。

## Release署名鍵

Release APK は次の2ファイルを使って署名します。

- `.tools/android-signing/coffeeeagle-reader-release.jks`
- `.tools/android-signing/CoffeeEagle.Reader.Signing.props`

`.tools/` は Git 管理外です。別PCや別開発環境に移る場合は、この2ファイルを同じパスへ復元してください。鍵を作り直すと Android は別署名アプリとして扱うため、インストール済みアプリを上書き更新できません。Google Cloud の Android OAuth クライアントに登録する SHA-1 も変わります。

初回だけ鍵を作る場合:

```powershell
.\scripts\android\New-CoffeeEagleReaderKeystore.ps1
```

既存鍵がある状態で `-Force` を使うのは、署名を意図的に捨てる時だけです。

## Build

通常環境:

```powershell
dotnet build .\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj -c Release -f net10.0-android
```

この環境で確認済みの SDK/JDK 指定:

```powershell
dotnet build .\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj -c Release -f net10.0-android -p:AndroidSdkDirectory="C:\work\CoffeeBook\COFFEEBOOK\.tools\android-sdk" -p:JavaSdkDirectory="C:\Users\coffe\.antigravity\extensions\redhat.java-1.54.0-win32-x64\jre\21.0.10-win32-x86_64"
```

生成APK:

```text
src\CoffeeEagle.Reader\bin\Release\net10.0-android\net.coffeewebjp.coffeeeagle.reader-Signed.apk
```

## Install

端末へ接続:

```powershell
& "C:\Users\coffe\Downloads\platform-tools-latest-windows\platform-tools\adb.exe" connect 192.168.1.15:44983
```

インストール:

```powershell
& "C:\Users\coffe\Downloads\platform-tools-latest-windows\platform-tools\adb.exe" install -r ".\src\CoffeeEagle.Reader\bin\Release\net10.0-android\net.coffeewebjp.coffeeeagle.reader-Signed.apk"
```

起動:

```powershell
& "C:\Users\coffe\Downloads\platform-tools-latest-windows\platform-tools\adb.exe" shell monkey -p net.coffeewebjp.coffeeeagle.reader -c android.intent.category.LAUNCHER 1
```

## Version

現在値:

- `ApplicationDisplayVersion`: `0.1.1`
- `ApplicationVersion`: `2`

配布用に再インストールする時は、必要に応じて `ApplicationVersion` を増やします。同じ署名鍵を使っていれば、Android上で上書き更新できます。
