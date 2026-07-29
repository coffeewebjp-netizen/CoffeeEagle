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

## 公式ドキュメント

- Enable Google Drive API: https://developers.google.com/workspace/drive/api/quickstart/java#enable_the_api
- Configure OAuth consent: https://developers.google.com/workspace/guides/configure-oauth-consent
- Create Android OAuth client: https://developers.google.com/workspace/guides/create-credentials#android
- Choose Drive scopes: https://developers.google.com/workspace/drive/api/guides/api-specific-auth
