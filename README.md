# CoffeeEagle

CoffeeEagle は、PC版 EAGLE のライブラリを Android から軽く閲覧するための非商用ビューアです。

## 方針

PC版は作りません。正本管理、編集、タグ付け、サムネイル生成は既存の EAGLE PC版に任せ、Android版は Google Drive などで同期済みの `.library` フォルダを読み取る専用ビューアとして作ります。

詳しい設計は [DESIGN.md](DESIGN.md) に整理しています。

運用メモ:

- [Android Release](docs/ANDROID_RELEASE.md)
- [Google Drive Credentials](docs/GOOGLE_DRIVE_CREDENTIALS.md)

## 現在の実装

- Android専用 .NET MAUI Reader: `src/CoffeeEagle.Reader`
- EAGLE `.library` フォルダを Android のフォルダ選択から追加
- Google Drive Provider上の指定 `.library` フォルダを追加
- 明示的な追加/更新時だけ `images/*.info/metadata.json` を走査
- ライブラリ切り替え、フォルダ切り替え、タグ選択
- 名前部分一致検索、`#タグ` 検索
- EAGLE側のサムネイルを使ったグリッド表示
- 全画面ビューアで左右スワイプ移動

## Build

Debug:

```powershell
dotnet build .\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj -f net10.0-android
```

Release署名鍵を初回だけ作成:

```powershell
.\scripts\android\New-CoffeeEagleReaderKeystore.ps1
```

Release APK:

```powershell
dotnet build .\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj -c Release -f net10.0-android
```

Release署名鍵は `.tools/android-signing` 配下に生成されます。このフォルダはGit管理外なので、別PCへ移る時は `coffeeeagle-reader-release.jks` と `CoffeeEagle.Reader.Signing.props` を同じパスへ復元してください。

