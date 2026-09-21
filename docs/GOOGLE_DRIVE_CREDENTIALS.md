# Google Drive Credentials

CoffeeEagle は次の2経路をサポートします。

- Android の Storage Access Framework (SAF) + Google Drive Provider
- Google Drive API + OAuth + 明示フォルダID

SAF経路ではGoogle CloudのOAuthクライアントは不要です。Drive API経路では、この文書に記載した登録済みAndroid OAuthクライアントを使います。

## 現在のアプリ情報

- Android package name: `net.coffeewebjp.coffeeeagle.reader`
- Release署名 SHA-1: `15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9`
- Android OAuth client ID: `327808944898-qr1qd5imhe3ddp56feng1kmpkpqnq10c.apps.googleusercontent.com`
- 署名鍵: `.tools/android-signing/coffeeeagle-reader-release.jks`
- 署名 props: `.tools/android-signing/CoffeeEagle.Reader.Signing.props`

`package name` と `SHA-1` は Google Cloud の Android OAuth クライアントに登録する値です。署名鍵を作り直すと SHA-1 が変わるため、同じクレデンシャルを使えなくなります。

## アプリ更新時の再登録

開発PC、アプリの表示バージョン、AndroidのversionCodeが変わっても、Google Cloud側の登録し直しは不要です。次の3点を維持します。

- package nameを`net.coffeewebjp.coffeeeagle.reader`から変更しない
- [Android署名手順](ANDROID_RELEASE.md)の共通Release署名を使い、SHA-1を変更しない
- 登録済みOAuth client IDとリダイレクトURIを変更しない

共通署名による上書きインストールでは、端末に保存したGoogle Driveのフォルダ設定と認証情報もそのまま引き継がれます。Google側で権限が取り消された場合や認証期限切れの場合は、アプリ内で再接続します。Google CloudプロジェクトやOAuthクライアントの再作成は不要です。

再登録が必要になるのは、package nameまたは署名鍵を意図的に変更して別のアプリIDとして配布する場合だけです。

## Google Cloud 登録手順

1. Google Cloud Console で CoffeeEagle 用のプロジェクトを作成または選択する。
2. Google Drive API を有効化する。
3. OAuth consent screen を設定する。
4. テスト中は External の Testing で、自分の Google アカウントを test user に入れる。
5. Scopes は最小権限から選ぶ。
6. OAuth client ID を `Android` として作成する。
7. Package name に `net.coffeewebjp.coffeeeagle.reader` を入れる。
8. SHA-1 certificate fingerprint に `15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9` を入れる。
9. 発行された Android OAuth client ID をアプリ設定へ追加する。

登録済み client ID:

```text
327808944898-qr1qd5imhe3ddp56feng1kmpkpqnq10c.apps.googleusercontent.com
```

## Scope 方針

まずは `drive.file` を検討します。ただし、ユーザーが指定した既存の EAGLE `.library` フォルダを再帰的に読む用途では、権限不足になる可能性があります。その場合は `drive.readonly` が実装上は単純ですが、Google の restricted scope に該当するため、公開配布時は審査や説明が重くなります。

利用方針:

1. SAF + Google Drive Provider、またはDrive API + OAuth + 明示フォルダ指定でライブラリを追加する。
2. Drive APIの索引作成は追加/更新時だけに限定し、通常の検索、タグ切り替え、閲覧では保存済み索引を使う。
3. 2回目以降の更新は保存済み同期状態と`mtime.json`を照合し、追加・変更されたアセットだけを再取得する。

## フォルダ連携で一覧が取得できない場合

AndroidのDrive Providerは、フォルダ選択画面で見えていてもアプリへの子一覧を空で返すことがある。
`images`が見つからない、または`mtime`には件数があるのに`.info`が0件になる場合は、Drive上の同じ`.library`フォルダのURLを確認し、`Google Drive APIフォルダ追加`から接続する。
元のDriveファイルを移動・削除したり、アプリをアンインストールする必要はない。
API側で更新と件数を確認してから、不要な古いProvider登録だけを整理する。

Reader0.2.4では、更新情報が非空なのにProviderの一覧が空の場合は同期を中止して保存済み一覧を保持する。
複数のAPIライブラリを登録しても、更新時は各ライブラリ自身のフォルダIDを使用する。最後に追加したフォルダの入力値は新規登録時だけ使用する。

同期の回帰検証: `dotnet run --project tests/CoffeeEagle.Sync.Tests/CoffeeEagle.Sync.Tests.csproj -c Release`。

2026-09-21の実機確認（ORC-20260921-018）では、Reader0.2.4を既存データを保持して上書きし、Providerで空一覧になったライブラリを同じDriveフォルダのAPI登録で復旧した。
31個の`.info`・metadataを読み取り失敗0で取得し、表示対象20件を回復。再更新は31件を再利用して約4秒、別ライブラリは68件を保持したまま93件を再利用して約3秒で完了した。
既存のスマホ保存音声366.47 MBと現在の6曲の選択を確認し、元ファイル・Watch側データには変更を加えていない。検証後に空の旧Provider登録だけを一覧から外した。

## 動画・音声のサイズや種類が更新されない場合

Reader0.2.4以前では、Driveへ本体が届く前にサムネイルだけを読み取ると、動画・音声が小さな画像として登録される場合があった。その後`mtime.json`に変化がないと、更新ボタンでも誤った登録を再利用していた。

Reader0.2.5は、metadataの拡張子（またはファイル名）から動画・音声の本体を選び、本体未取得の項目は再試行対象として保持する。以前の正常な本体情報があれば一時的に保持する。次の通常更新ではmtimeが同じでも再取得する。旧版の種類不一致・サムネイル代用の登録も通常更新で読み直すため、ライブラリ再登録やデータ消去は不要。

正常な未変更項目は従来どおり差分再利用する。Driveへのアップロード自体が完了していない場合は、完了後にもう一度更新する。既存のオフライン保存音声やWatch転送済みファイルを自動的に置き換える変更ではない。

回帰テストでは、動画・音声それぞれについてアップロード途中から同じmtimeでの復旧、旧Active登録の修復、正常な本体の一時保持、復旧後の差分再利用を検証する。ファイル名のみのmetadataも対象。

## 公式ドキュメント

- Enable Google Drive API: https://developers.google.com/workspace/drive/api/quickstart/java#enable_the_api
- Configure OAuth consent: https://developers.google.com/workspace/guides/configure-oauth-consent
- Create Android OAuth client: https://developers.google.com/workspace/guides/create-credentials#android
- Choose Drive scopes: https://developers.google.com/workspace/drive/api/guides/api-specific-auth
