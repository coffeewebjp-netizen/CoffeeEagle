# Android Signing and Cross-PC Update

この文書を CoffeeEagle Reader のAndroid署名・更新手順の正本とする。

## 目的

どの開発PCでも同じAndroidアプリとしてRelease APKを作り、端末内データを消さずに既存のCoffeeEagleを更新できる状態を維持する。

AndroidのアプリIDはpackage nameと署名証明書の組み合わせで決まる。署名が変わると上書き更新できず、アンインストールが必要になり、アプリ内のライブラリ設定や索引が消える可能性がある。

## 正本のアプリID

- Package name: net.coffeewebjp.coffeeeagle.reader
- Release署名 SHA-1: 15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9
- ApplicationDisplayVersion: 0.1.2
- ApplicationVersion / Android versionCode: 3

このpackage nameとSHA-1は既存の端末インストールおよびGoogle Cloud Android OAuth clientと一致する。変更しないこと。

## Gitだけでは完結しないもの

秘密鍵とパスワードは公開リポジトリへ入れない。次の2ファイルを暗号化バックアップ、秘密管理ストレージ、または安全な共有ドライブで別管理し、更新を行う各PCへ復元する。

~~~text
coffeeeagle-reader-release.jks
CoffeeEagle.Reader.Signing.props
~~~

リポジトリ内の復元先は次のとおり。

~~~text
.tools\android-signing\coffeeeagle-reader-release.jks
.tools\android-signing\CoffeeEagle.Reader.Signing.props
~~~

.tools、*.jks、*.Signing.propsはGit管理外である。したがって「どのPCでも更新可能」とは、リポジトリのcloneに加えて正本の署名バックアップへアクセスできる開発環境を意味する。

## 新しい開発PCの準備

### 1. SDK / JDK

ビルドは次の順序でAndroid SDKとJDKを探す。

1. コマンド引数で指定したパス
2. ANDROID_SDK_ROOT / ANDROID_HOME / JAVA_HOME
3. このリポジトリの .tools
4. 隣接する CoffeeBook\COFFEEBOOK\.tools
5. ビルドスクリプト実行時はローカルのRed Hat Java拡張JRE

推奨のリポジトリローカル配置:

~~~text
.tools\android-sdk\
.tools\jdk-17\current\
~~~

通常の dotnet build でも [Directory.Build.props](../src/CoffeeEagle.Reader/Directory.Build.props) が上記の標準配置を自動解決する。特殊な配置では後述のビルドスクリプトにパスを渡す。

### 2. 共通署名を復元

正本2ファイルを置いた安全なフォルダを指定する。

~~~powershell
$env:COFFEE_EAGLE_SIGNING_SOURCE = "E:\secure\CoffeeEagle"
.\scripts\android\Restore-CoffeeEagleReaderSigning.ps1
~~~

または:

~~~powershell
.\scripts\android\Restore-CoffeeEagleReaderSigning.ps1 -SourceDir "E:\secure\CoffeeEagle"
~~~

復元スクリプトは、署名props内のkeystoreパスを現在のclone先へ書き換え、証明書SHA-1が正本と一致する場合だけ保存する。

すでに復元済みのファイルを置換する場合だけ -Force を使う。

## ビルド

推奨:

~~~powershell
.\scripts\android\Build-Install-CoffeeEagleReader.ps1
~~~

このスクリプトはRelease APKをビルドし、APK署名が正本SHA-1と一致することを検証する。

直接ビルドする場合:

~~~powershell
dotnet build .\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj -c Release -f net10.0-android
~~~

共通署名がないReleaseビルドは、誤署名APKを作らないようプロジェクト側で失敗させる。

特殊なツール配置:

~~~powershell
.\scripts\android\Build-Install-CoffeeEagleReader.ps1 -AndroidSdkDirectory "D:\Android\Sdk" -JavaSdkDirectory "D:\Java\jdk-17"
~~~

生成APK:

~~~text
src\CoffeeEagle.Reader\bin\Release\net10.0-android\net.coffeewebjp.coffeeeagle.reader-Signed.apk
~~~

## 端末へ安全に更新

接続が1台だけなら:

~~~powershell
.\scripts\android\Build-Install-CoffeeEagleReader.ps1 -Install -Launch
~~~

複数のadb接続がある場合:

~~~powershell
.\scripts\android\Build-Install-CoffeeEagleReader.ps1 -Install -Launch -DeviceSerial "192.168.1.4:43547"
~~~

スクリプトは adb install -r -d を使い、既存データを維持して更新する。署名が違う場合でも自動アンインストールはしない。

正式な更新ごとに ApplicationVersion を増やす。表示用リリース番号を変える場合は ApplicationDisplayVersion も更新する。

## やってはいけないこと

- 他PCでDebug APKを作り、既存のRelease版へ上書きしない。
- 署名不一致時に原因確認前のアンインストールをしない。
- 既存アプリ更新用として新しいkeystoreを生成しない。
- .toolsの署名ファイルやパスワードをGitへ追加しない。

New-CoffeeEagleReaderKeystore.ps1 は別の新規アプリ署名を意図的に作る場合だけ、次の明示指定で実行できる。

~~~powershell
.\scripts\android\New-CoffeeEagleReaderKeystore.ps1 -NewApplicationIdentity
~~~

既存CoffeeEagleの更新環境では、必ず Restore-CoffeeEagleReaderSigning.ps1 を使用する。

## 手動検証

APKの証明書:

~~~powershell
keytool -printcert -jarfile .\src\CoffeeEagle.Reader\bin\Release\net10.0-android\net.coffeewebjp.coffeeeagle.reader-Signed.apk
~~~

出力のSHA-1は必ず次と一致すること。

~~~text
15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9
~~~

INSTALL_FAILED_UPDATE_INCOMPATIBLE の場合は作業を止め、Debug APKを使っていないか、復元した鍵が正本かを確認する。
