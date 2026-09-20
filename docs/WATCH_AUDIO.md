# Pixel Watch オフライン音声

CoffeeEagle 0.2.0 は、スマホに保存した音声を Wear OS アプリへ送り、WatchとBluetoothイヤホンだけで再生する機能を追加する。Google Driveへのログインとライブラリ索引はスマホが担当する。WatchへDriveの認証情報やライブラリ全体を送らない。

## 使い方

1. スマホに `CoffeeEagle-0.2.0-phone.apk`、Watchに `CoffeeEagle-0.2.0-watch.apk` を入れる。両方とも既存CoffeeEagleと同じ署名を使用する。既存アプリをアンインストールしない。
2. スマホの本棚でフォルダ・タグ・検索を選び、「音声の持ち出し / Watch」を開く。「今の絞り込み」には対応する音声だけを表示する。
3. 音声をチェックして「選択を端末に保存」を押す。保存後は「端末に保存済み」で聴ける。Watch転送ボタンから必要なスマホ保存も一緒に実行できる。
4. WatchでCoffeeEagle Audioを開き「音声を受信」を押す。両端末を近くに置き、両アプリを表示したまま、スマホから「選択をWatchへ送る」を押す。
5. スマホ側の保存確認後、Watchで「受信を終了」を押すと保存一覧が更新される。イヤホンをWatchへ接続し、音声名をタップする。転送済み音声の再生にはスマホもインターネットも不要。
6. Watchでは再生・一時停止、前後の音声、15秒戻し/送りに対応。最後の音声まで名前順に連続再生する。再生位置は15秒ごとと一時停止時に保存する。画面を閉じても音声用サービスとメディア通知で再生する。

Watchの設定メニューにイヤホン接続、保存一覧更新、容量上限、再生終了がある。音声名の長押しでWatch内のコピーだけを削除する。イヤホン未接続時は再生を開始せず、切断通知・音声フォーカス喪失時には一時停止する。再接続後の再生は明示操作で再開する。

## 保存と更新

- 初期上限はスマホ20 GiB、Watch2 GiB。これは使用量の上限で、事前に容量を確保するものではない。スマホでは数値指定、Watchでは0.5 / 1 / 2 / 4 / 8 GiBに変更できる。実際の空き容量も確認し、作業領域として16 MiB以上を残す。
- 1ファイルは最大2 GiB、保存一覧は最大10,000件。保存時の一時ファイル用に、置換前の音声とは別の空き容量が必要。
- 保存先は各アプリの専用永続領域 `offline-audio-v1`。通常の閲覧キャッシュとは独立し、自動容量整理で選択済み音声を消さない。アンインストール/アプリデータ消去で消える。スマホの大容量音声コピーはAndroidの自動バックアップ/端末移行対象から除外する。
- スマホの「選択を端末から削除」はスマホだけ、Watchの長押し削除はWatchだけに作用する。EAGLE/Driveの元データには書き込まない。元データが消えても保存済み音声は自動削除しない。
- 更新は手動。本棚の「更新」で索引を更新し、持ち出し画面の「更新あり」を保存・転送し直す。通常の閲覧キャッシュを経由せず元ファイルを再取得する。未変更の保存済み音声は再ダウンロードしない。
- 対応拡張子: MP3、M4A、M4B、AAC、OGG、OGA、OPUS、FLAC、WAV、AMR。実際のコーデック対応は端末のAndroidデコーダーに依存する。変換処理はないため、最初の実機確認は短いMP3で行う。動画・画像・リモコン機能はこの版のWatch対象に含まない。

## 中断時の動作

保存・転送は一曲ずつ実行する。画面を離れる、中止する、容量が足りない、通信が切れる場合は停止し、完了済みの曲は保持する。再実行すると検証済みの同じ音声は省略し、途中の曲からファイル単位で再送する。バイト位置からの再開ではない。

受信中データは一時ファイルへ書く。長さとSHA-256を確認してから保存一覧を原子的に置き換え、Watchから保存済み応答を返す。送信完了は、その応答をスマホが受け取った場合だけ表示する。応答を受け取れず再送した場合もWatch上の同一データを再検証して応答できる。更新失敗時は前の検証済みコピーを保持する。

## 実装の境界

- `src/CoffeeEagle.Offline`: Android非依存の保存・容量・転送ヘッダー。`catalog.json` はバージョン1。既存 `coffeeeagle-state.json` の形式を変更しない。将来バージョンや破損した一覧は自動初期化しない。
- `src/CoffeeEagle.WearTransport`: Reader/Wearの両ビルドに含める同じC#実装。Wearable Data LayerのChannelClientでストリームを送り、MessageClientで確認応答する。動的な受信能力 `coffeeeagle_audio_receive_v1` は受信画面中だけ公開する。
- `/coffeeeagle/audio/v1/<transfer-id>`: 4バイトbig-endianのJSON長、最大4096バイトのJSON、指定長の音声。JSONはバージョン、転送ID、音声ID、表示名、リビジョン、拡張子、長さ、SHA-256。元URIやトークンは含めない。
- `/coffeeeagle/audio-ack/v1/<transfer-id>`: `ready` / `stored` またはエラー。送信元nodeと転送IDを照合する。Watch側は近くの相手かも確認し、スマホ側は送信中も近接状態を監視する。
- Data Layer自体はGoogleのクラウド中継に切り替わり得る。近接確認は通信経路を固定する保証ではなく、ローカル専用通信という表示はしない。大容量転送の速度と電池消費は実機測定が必要。
- 受信は前景画面に限定し、暗黙のバックグラウンド受信・同期は行わない。Watchの再生は別のforeground mediaPlayback serviceが担当し、ネットワークやスマホに依存しない。
- Google Play servicesは `Xamarin.GooglePlayServices.Wearable 119.0.0.3` に固定する。MAUI 10.0.20のAndroidX依存と整合する版。現行MAUIのまま120.0.1.2を追加するとAndroidX KTXとの重複クラスが生じたため、AndroidX一式の更新はこの機能に含めない。

## ビルド・配布

```powershell
dotnet run --project .\tests\CoffeeEagle.Offline.Tests\CoffeeEagle.Offline.Tests.csproj -c Release
.\scripts\android\Build-CoffeeEagleAudioPair.ps1
```

共通Release署名とSDK/JDKは [ANDROID_RELEASE.md](ANDROID_RELEASE.md) の既存手順を使う。新しい署名鍵を作らない。ペアビルドは端末へのインストールをしない。出力は `dist/watch-audio/` 内のスマホAPK・Watch APK・SHA256SUMS.txt。APKはGitに含めない。

端末への更新が明示承認され、両端末がadbで接続済みの場合:

```powershell
.\scripts\android\Build-Install-CoffeeEagleReader.ps1 -Target Reader -SkipBuild -Install -Launch -DeviceSerial <phone-serial>
.\scripts\android\Build-Install-CoffeeEagleReader.ps1 -Target Watch -SkipBuild -Install -Launch -DeviceSerial <watch-serial>
```

スクリプトは署名SHA-1と端末種別を確認し、Watch用APKをスマホへ、スマホ用APKをWatchへ入れる操作を拒否する。同一package IDと署名がData Layer連携の条件。ReaderはversionCode 6、Wearは7。Google Playへの公開やAPK配布先の変更は行っていない。

## 検証

自動検証は実ファイルと疑似中断ストリームを使い、保存/再起動、未変更省略、リビジョン置換、中断保護、SHA/長さ、容量上限、実空き容量、キャンセル、破損修復、削除範囲、一覧破損/将来版、起動時復旧、同時保存、上限永続化、分割ヘッダー、不正サイズ・パス・プロトコルを確認する。

### 2026-09-20 Pixel 9aでの確認

- Android 17 / API 37のPixel 9aをADBで確認。0.1.4 / versionCode 5から署名済み0.2.0 / versionCode 6への上書きインストールと起動に成功。アンインストールやデータ消去は行っていない。
- 更新前後で既存ライブラリ、選択フォルダ、表示件数、列数が一致。「音声の持ち出し / Watch」が表示され、音声一覧・選択件数/容量・初期上限20 GBを確認。起動後の対象プロセスのエラーログにエラーはなかった。
- 既存DriveライブラリのMP3を1件選び保存を試行。Driveの既存認証が期限切れ/取り消しとして拒否され、画面には再接続案内、保存量0 MBを表示した。ライブラリ設定を保持したまま既存の再接続フローを開いた。既存認証処理は拒否された更新トークンを空にする仕様。Drive再認証と実データの保存・再生は未確認。
- WatchのADB接続は未確立。Watchへのインストール・転送・再生は未確認。スマホ更新成功だけでWatch対応の実機検証完了とは扱わない。

次は端末での残りの確認事項:

- Pixel 9aでDriveを再接続し、音声を保存・アプリ再起動後も再生できる。
- Watchで受信を開始し、短いMP3を送り、長さ/SHA確認後だけスマホに完了表示される。
- 同じ音声の再送が省略され、変更版は更新される。
- 転送中断/容量不足/受信画面を離れる操作で、前の音声が残り再送できる。
- スマホのBluetoothを切り、Watchのネット接続も切った状態で、Watchに接続したイヤホンから再生できる。
- 画面消灯後も再生が続き、イヤホン切断・他の音声/通話によるフォーカス喪失で一時停止する。
- Watch再起動後の一覧と再生位置、丸形画面の欠け・文字サイズ・操作性を確認する。
- 実測した転送時間と電池消費を記録する。

## 参考

- [Wearable Data Layer](https://developer.android.com/training/wearables/data/overview)
- [ChannelClient](https://developers.google.com/android/reference/com/google/android/gms/wearable/ChannelClient)
- [Background audio](https://developer.android.com/media/platform/mediaplayer/background)
- [App-specific storage](https://developer.android.com/training/data-storage/app-specific)
- [Microsoft Wearable bindings](https://www.nuget.org/packages/Xamarin.GooglePlayServices.Wearable/119.0.0.3)
