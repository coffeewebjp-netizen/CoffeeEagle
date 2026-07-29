# CoffeeEagle Design

## 基本判断

CoffeeEagle は Android版だけを作る。

CoffeeBook は PC版で本を作り、Android版で読む構成だったが、CoffeeEagle では PC版 EAGLE がすでに正本管理アプリとして存在する。CoffeeEagle が PC版を持つと、EAGLE と二重管理になり、タグ、フォルダ、サムネイル、画像ファイルの同期責務が増える。したがって CoffeeEagle は EAGLE ライブラリを読む Android ビューアに絞る。

## CoffeeBook から引き継ぐ原則

- 開発環境が違っても使えるよう、アプリ固有のPC前提処理を増やさない。
- Android側は EAGLE の `.library` フォルダを標準的な DocumentTree/SAF 経由で読む。Google Drive上のライブラリも、AndroidのGoogle Drive Providerから指定フォルダを選ぶ。
- 通常の移動、検索、フォルダ切り替え、ビューアから戻る操作で、ライブラリ全体を読み直さない。
- 永続データの全読み込みは、起動時、ライブラリ切り替え、明示的な更新だけに限定する。
- EAGLE PC版が生成済みのサムネイルを優先して使い、Android側で独自サムネイルを大量生成しない。
- 画像本体は原則コピーしない。表示時に同期済みライブラリ内のファイルを読む。
- キャッシュ済みかどうかを永続フラグで抱えず、必要に応じて実ファイルと保存済み索引から判断する。

## 対象データ

EAGLE の `.library` フォルダを対象にする。

想定構造:

```text
Sample.library/
  metadata.json
  mtime.json
  images/
    <asset-id>.info/
      metadata.json
      thumbnail.png
      original-or-preview-file
```

Android側は次の情報を索引として保存する。

- ライブラリ名、DocumentTree URI、source kind
- フォルダID、フォルダ名、親子関係
- アセットID、名前、ファイル名、タグ、フォルダID
- サムネイルURI、表示用ファイルURI
- サイズ、作成日、更新日など取得できる軽量メタデータ
- 更新時の差分検知に使う `mtime.json` のアセットIDと件数
- `.info` ごとの同期状態（有効、削除済み、metadata欠落、読取失敗）と更新stamp

## Google Drive フォルダ

初期実装では Android の Storage Access Framework で Google Drive Provider のフォルダを選ぶ。アプリ内では `Google Driveフォルダ追加` と `端末/同期フォルダ追加` を分け、選択された URI の provider を見て source kind を保存する。

この方式ならGoogle API認証をアプリに追加せず、端末に入っているGoogle DriveアプリとAndroid標準の権限管理に任せられる。もし実機でDrive Providerが `.library` フォルダの再帰読み取りを許可しない場合は、CoffeeBook Readerと同じブラウザOAuth + PKCEのDrive API方式へ切り替える。

更新時はルートの `mtime.json` があれば先に読み、Drive Providerの再帰列挙で見えた `images/*.info` と照合する。`mtime.json` に存在するのに列挙されないIDは、`images/<asset-id>.info` として直接Document URI候補を問い合わせ、取れる場合は索引へ回収する。`mtime` では差分検知と直接参照の試行まではできるが、Google Drive Providerが一覧も直接参照も古い場合はファイル本体を発見できない。その場合はDrive API方式へ切り替える。

Drive API経路では、指定されたEAGLE `.library` フォルダIDからDrive REST APIで `metadata.json`、`mtime.json`、`images/*.info` を直接列挙する。画像や音声本体は索引作成時にはダウンロードせず、表示・再生時だけ端末キャッシュへ取得する。

## 機能スコープ

初期スコープ:

- Google Driveフォルダ追加、端末/同期フォルダ追加、ライブラリ切り替え、明示更新
- フォルダ切り替え
- 名前部分一致検索
- `#tag` 形式のタグ検索
- タグ一覧からの絞り込み
- サムネイルグリッド表示
- グリッド密度切り替え
- 全画面表示
- 左右スワイプで次/前の画像へ移動
- 音声ファイルのアプリ内再生、動画ファイルの外部アプリ連携

後続候補:

- 複数タグAND/ORフィルタ
- お気に入りやローカル閲覧履歴
- Drive同期状態の軽量表示
- 動画/PDFなど画像以外の外部アプリ連携
- サムネイル欠落時だけの小規模オンデマンドキャッシュ

## パフォーマンス方針

索引作成は重い処理なので、ライブラリ追加または更新ボタンからだけ実行する。検索、タグ切り替え、フォルダ切り替え、ビューア遷移は保存済み索引をメモリ上でフィルタする。

初回索引では全 `.info` のmetadataを読み、`isDeleted` を含む同期状態を保存する。2回目以降は `images` の軽量なフォルダ一覧と `mtime.json` を保存済み状態に照合し、追加または更新された `.info` だけmetadataを再読込する。未変更の有効アセットと削除済みアセットは保存済み状態を再利用し、物理一覧から消えたアセットは索引から削除する。`mtime.json` のID集合は完全一覧とは限らないため、mtimeに存在しないことだけを削除根拠にはしない。

更新画面は探索済み `.info` 件数を全体作業量として進捗表示し、追加・変更・削除・変更なしの内訳を表示する。端末フォルダのDocumentProvider走査はバックグラウンドで実行し、進捗通知だけをUIスレッドへ戻す。

ビューアは現在の検索結果リストを受け取り、スワイプではそのリスト内のインデックスだけを変更する。ページ移動のたびにストレージ走査をしない。

Android側で保存するのは軽量な `coffeeeagle-state.json` だけにする。画像とサムネイルは EAGLE ライブラリ内の既存ファイルを参照し、明示的な必要が出るまでコピーしない。`TreeUri` と source kind を保存するので、Google Drive Provider経由のライブラリか端末/同期フォルダ経由のライブラリかを区別できる。

## 署名鍵

Release APK は `.tools/android-signing/CoffeeEagle.Reader.Signing.props` を読み込んで署名する。この props と `.tools/android-signing/coffeeeagle-reader-release.jks` は Git 管理外にし、別PCでは同じ場所へ復元してからReleaseビルドする。作り直すとAndroid上では別アプリ署名扱いになり、上書き更新できなくなるため、作成後は必ずバックアップする。
