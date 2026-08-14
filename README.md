# CoffeeEagle

CoffeeEagle は、PC版 EAGLE のライブラリを Android から軽く閲覧するための非商用ビューアです。

## 方針

PC版は作りません。正本管理、編集、タグ付け、サムネイル生成は既存の EAGLE PC版に任せ、Android版は Google Drive などで同期済みの .library フォルダを読み取る専用ビューアとして作ります。

詳しい設計は [DESIGN.md](DESIGN.md) に整理しています。

運用メモ:

- [Android Signing and Cross-PC Update](docs/ANDROID_RELEASE.md)
- [Google Drive Credentials](docs/GOOGLE_DRIVE_CREDENTIALS.md)

## 現在の実装

- Android専用 .NET MAUI Reader: src/CoffeeEagle.Reader
- EAGLE .library フォルダを Android のフォルダ選択から追加
- Google Drive Provider上の指定 .library フォルダを追加
- Google Drive API + OAuthで指定フォルダIDの .library を追加
- 初回だけ images/*.info/metadata.json を分類し、以後は mtime.json と保存済み同期状態から追加・変更分だけを再読込
- 削除済みアセットの除外、物理削除の反映、更新中の全体進捗バー
- ライブラリ切り替え、フォルダ切り替え、タグ選択
- 名前部分一致検索、#タグ 検索
- EAGLE側のサムネイルを使ったグリッド表示
- 全画面ビューアで左右スワイプ移動
- Androidネイティブプレイヤーによる動画再生
- 起動時にアプリアイコンを拡大・フェードアウトして本棚へ遷移

## Build / Update

通常の開発ビルド:

~~~powershell
dotnet build .\src\CoffeeEagle.Reader\CoffeeEagle.Reader.csproj -f net10.0-android
~~~

既存Androidアプリをどの開発PCからでも更新する場合は、同じRelease署名鍵を安全なバックアップから復元する。新しい鍵は生成しない。

~~~powershell
$env:COFFEE_EAGLE_SIGNING_SOURCE = "E:\secure\CoffeeEagle"
.\scripts\android\Restore-CoffeeEagleReaderSigning.ps1
~~~

Release APKのビルド、正本署名の検証、端末への上書き更新:

~~~powershell
.\scripts\android\Build-Install-CoffeeEagleReader.ps1 -Install -Launch
~~~

SDK/JDKは環境変数、リポジトリローカル .tools、隣接CoffeeBookの順で自動解決する。署名鍵は秘密情報なのでGit管理外であり、別PCでは正本2ファイルへのアクセスだけが別途必要になる。詳細と署名SHA-1は [Android Signing and Cross-PC Update](docs/ANDROID_RELEASE.md) を参照。
