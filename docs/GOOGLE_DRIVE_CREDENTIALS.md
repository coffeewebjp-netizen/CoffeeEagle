# Google Drive Credentials

CoffeeEagle の初期実装は Android の Storage Access Framework (SAF) と Google Drive Provider を使います。この経路では Google Cloud の OAuth クライアントは不要です。端末に入っている Google Drive アプリのフォルダ選択と Android の永続URI権限に任せます。

Google Drive Provider で `.library` フォルダの再帰読み取りが安定しない場合だけ、Drive API + OAuth 方式へ切り替えます。その場合の登録手順をここに残します。

## 現在のアプリ情報

- Android package name: `net.coffeewebjp.coffeeeagle.reader`
- Release署名 SHA-1: `15:DA:71:1D:E4:FB:EB:B4:B7:38:18:DC:56:C9:21:53:6F:92:B2:C9`
- Android OAuth client ID: `327808944898-qr1qd5imhe3ddp56feng1kmpkpqnq10c.apps.googleusercontent.com`
- 署名鍵: `.tools/android-signing/coffeeeagle-reader-release.jks`
- 署名 props: `.tools/android-signing/CoffeeEagle.Reader.Signing.props`

`package name` と `SHA-1` は Google Cloud の Android OAuth クライアントに登録する値です。署名鍵を作り直すと SHA-1 が変わるため、同じクレデンシャルを使えなくなります。

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

当面の優先順位:

1. SAF + Google Drive Provider で読む。
2. 足りない場合、Drive API + OAuth + 明示フォルダ指定へ切り替える。
3. Drive API を使う場合も、索引作成は追加/更新時だけに限定し、通常操作では Drive API を叩かない。

## 公式ドキュメント

- Enable Google Drive API: https://developers.google.com/workspace/drive/api/quickstart/java#enable_the_api
- Configure OAuth consent: https://developers.google.com/workspace/guides/configure-oauth-consent
- Create Android OAuth client: https://developers.google.com/workspace/guides/create-credentials#android
- Choose Drive scopes: https://developers.google.com/workspace/drive/api/guides/api-specific-auth
