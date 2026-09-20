# Pixel Watch オフライン音声

CoffeeEagle 0.2.1 は、スマホに保存した音声を Wear OS アプリへ送り、WatchとBluetoothイヤホンだけで再生する。Google Driveへのログインとライブラリ索引はスマホが担当する。WatchへDriveの認証情報やライブラリ全体を送らない。

## 使い方

1. スマホに `CoffeeEagle-0.2.1-phone.apk`、Watchに `CoffeeEagle-0.2.1-watch.apk` を入れる。両方とも既存CoffeeEagleと同じ署名を使用する。既存アプリをアンインストールしない。
2. スマホの本棚でフォルダ・タグ・検索を選び、「音声の持ち出し / Watch」を開く。「今の絞り込み」には対応する音声だけを表示する。
3. 音声ごとにチェックしてWatch同期対象をONにする。選択はスマホが記憶し、フォルダ切替・アプリ再起動後も残る。必要なら「同期対象をスマホに保存」を押す。保存済み音声はサムネイルのタップでスマホでも聴ける。
4. Watchで「音声を受信」を押す。両端末を近くに置き、両アプリを表示したまま、スマホから「同期対象をWatchへ送る」を押す。必要なスマホ保存も一緒に実行する。「今の絞り込み」表示中は現在のライブラリ全体のONの音声、「端末に保存済み」表示中は保存済み音声のONが対象。画面の対象件数を確認する。「表示分のON / OFF」は表示中のファイルだけを切り替える。
5. 保存確認後、Watchで「受信を終了」を押すとサムネイル一覧が更新される。イヤホンをWatchへ接続し、カードをタップして再生する。サムネイルがない音声は音符カードを表示する。旧版で保存した音声の画像はスマホのライブラリ側から再送すると追加でき、同じ音声本体は再転送しない。
6. 再生画面のシークバーで位置を変更し、「音量」でWatchのメディア音量を調整する。再生・一時停止、前後の音声、15秒戻し/送りにも対応。最後まで名前順に連続再生する。位置は再生中15秒ごと・一時停止・シーク完了時に保存する。画面を閉じても音声用サービスとメディア通知で再生する。

Watchの設定メニューにイヤホン接続、保存一覧更新、容量上限、再生終了がある。カードの長押しでWatch内のコピーだけを削除する。イヤホン未接続時は再生を開始せず、切断通知・音声フォーカス喪失時には一時停止する。再接続後の再生は明示操作で再開する。

同期対象をOFFにしてもスマホ・Watch・Driveのファイルは削除しない。動画はWatch同期の対象外。自動定期同期はせず、送信ボタンで反映する。

オフライン再生でも音声のデコード、Bluetoothイヤホン通信、画面表示に電池を使う。ストリーミングの通信を避ける効果はあるが、電池消費がなくなるわけではない。聴いている間は画面を閉じ、転送が終わったら受信を終了する。持続時間と削減率は未測定。[Wear OSのメディア設計](https://developer.android.com/media/implement/surfaces/wear-os)も事前保存を推奨する。

## 保存と更新

- 初期上限はスマホ20 GiB、Watch2 GiB。これは使用量の上限で、事前に容量を確保するものではない。スマホでは数値指定、Watchでは0.5 / 1 / 2 / 4 / 8 GiBに変更できる。実際の空き容量も確認し、作業領域として16 MiB以上を残す。
- 1ファイルは最大2 GiB、保存一覧は最大10,000件。保存時の一時ファイル用に、置換前の音声とは別の空き容量が必要。
- 保存先は各アプリの専用永続領域 `offline-audio-v1`。通常の閲覧キャッシュとは独立し、自動容量整理で選択済み音声を消さない。アンインストール/アプリデータ消去で消える。スマホの大容量音声コピーはAndroidの自動バックアップ/端末移行対象から除外する。
- スマホの「対象のスマホ内コピーを削除」はスマホだけ、Watchの長押し削除はWatchだけに作用する。EAGLE/Driveの元データや同期対象の設定は消さない。元データが消えても保存済み音声は自動削除しない。
- 更新は手動。本棚の「更新」で索引を更新し、持ち出し画面の「更新あり」を保存・転送し直す。通常の閲覧キャッシュを経由せず元ファイルを再取得する。未変更の保存済み音声は再ダウンロードしない。
- 対応拡張子: MP3、M4A、M4B、AAC、OGG、OGA、OPUS、FLAC、WAV、AMR。実際のコーデック対応は端末のAndroidデコーダーに依存する。変換処理はないため、最初の実機確認は短いMP3で行う。動画・画像・リモコン機能はこの版のWatch対象に含まない。

## 中断時の動作

保存・転送は一曲ずつ実行する。画面を離れる、中止する、容量が足りない、通信が切れる場合は停止し、完了済みの曲は保持する。再実行すると検証済みの同じ音声は省略し、途中の曲からファイル単位で再送する。バイト位置からの再開ではない。

受信中データは一時ファイルへ書く。長さとSHA-256を確認してから保存一覧を原子的に置き換え、Watchから保存済み応答を返す。送信完了は、その応答をスマホが受け取った場合だけ表示する。応答を受け取れず再送した場合もWatch上の同一データを再検証して応答できる。更新失敗時は前の検証済みコピーを保持する。

## 実装の境界

- `src/CoffeeEagle.Offline`: Android非依存の保存・容量・転送ヘッダー。`catalog.json` はバージョン1。既存 `coffeeeagle-state.json` の形式を変更しない。将来バージョンや破損した一覧は自動初期化しない。
- `src/CoffeeEagle.WearTransport`: Reader/Wearの両ビルドに含める同じC#実装。Wearable Data LayerのChannelClientでストリームを送り、MessageClientで確認応答する。動的な受信能力 `coffeeeagle_audio_receive_v1` は受信画面中だけ公開する。
- `/coffeeeagle/audio/v1/<transfer-id>`: 4バイトbig-endianのJSON長、最大4096バイトのJSON、指定長の音声。JSONはバージョン、転送ID、音声ID、表示名、リビジョン、拡張子、長さ、SHA-256。元URIやトークンは含めない。
- 0.2.1は受信中に `coffeeeagle_audio_receive_v2` も公開し、対応相手には `/coffeeeagle/audio/v2/<transfer-id>` を使う。v2ヘッダーは最大32 KiB、任意のJPEG画像は16 KiBまで。旧相手にはv1を使い、新Watchは両方を受け付ける。応答形式は同じ。同じ音声が保存済みでも画像だけ更新できる。
- 画像はスマホで元画像の読み込みを4 MiB・復号寸法を制限し、長辺160px以下のJPEGへ縮小する。各端末の画像キャッシュは音声と分離し16 MiBまで、古い画像だけを整理する。Watchでも復号寸法を制限する。欠落・破損時は音符カードを表示し、音声を削除しない。
- Watch対象はスマホの `watch-targets-v1.json` にライブラリ/ファイルのハッシュID集合として保存する。初期値はOFF。既存音声カタログは変更しない。未知バージョンや破損した設定を勝手に初期化しない。
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

スクリプトは署名SHA-1と端末種別を確認し、Watch用APKをスマホへ、スマホ用APKをWatchへ入れる操作を拒否する。同一package IDと署名がData Layer連携の条件。0.2.1のReaderはversionCode 8、Wearは9。Google Playへの公開やAPK配布先の変更は行っていない。

Watchプロジェクトは `android-arm;android-arm64;android-x64` を明示する。今回のPixel Watch 5実機はAndroid 17でも `armeabi-v7a` のみ対応していた。64ビットのみの既定ビルドは `INSTALL_FAILED_NO_MATCHING_ABIS` でインストールできないため、32ビットARMを必ず含める。

## 検証

自動検証は実ファイルと疑似中断ストリームを使い、保存/再起動、未変更省略、リビジョン置換、中断保護、SHA/長さ、容量上限、実空き容量、キャンセル、破損修復、削除範囲、一覧破損/将来版、起動時復旧、同時保存、上限永続化、分割ヘッダー、不正サイズ・パス・プロトコルを確認する。

### 2026-09-20 Pixel 9a / Pixel Watch 5での確認

- Android 17 / API 37のPixel 9aをADBで確認。0.1.4 / versionCode 5から署名済み0.2.0 / versionCode 6への上書きインストールと起動に成功。アンインストールやデータ消去は行っていない。
- 更新前後で既存ライブラリ、選択フォルダ、表示件数、列数が一致。「音声の持ち出し / Watch」が表示され、音声一覧・選択件数/容量・初期上限20 GBを確認。起動後の対象プロセスのエラーログにエラーはなかった。
- 最初は既存Drive認証の期限切れ/取り消しで保存が停止した。既存の再接続後に索引更新が成功し、8.33 MiBのMP3を1曲スマホへ保存できた。既存認証処理は拒否された更新トークンを空にするが、ライブラリ設定は保持する。
- Pixel Watch 5（Android 17/API37）をADBペア設定し、32ビットARMを追加した署名済み0.2.0 / versionCode 7のインストール・起動に成功。Releaseビルドは警告0/エラー0、共通署名とAPK内のarmeabi-v7aを検証した。
- Watchの受信待機、スマホからの1曲転送、検証後の保存済み応答を両画面で確認。同一曲を再送するとWatchは「保存済みです」と応答し、一覧は1曲のまま。受信終了後の一覧と、アプリプロセス再起動後も1曲が残ることを確認した。
- Watchで保存曲の再生準備に成功し、3分56秒の長さを表示。イヤホン未接続では案内を表示してPAUSEDとなり、接続後はA2DP出力・MediaSession PLAYING・音声レンダラー再生中を確認。画面がDozingの間も再生中だった。聴感による音の確認は行っていない。
- スマホBluetooth/Watch Wi-Fiを一時的に切る確認は判定保留。記録時にWatch Wi-Fiは有効状態に戻っており、その間にイヤホン接続も外れてPAUSEDになったため、完全オフライン再生の成功とは扱わない。両端末の通信設定は有効状態へ復元済み。登録済みイヤホンへの再接続を試したが、接続確認には至っていない。
- 最初の8.33 MiB転送は開始から完了確認まで約76秒以内（途中のUI確認時間を含む上限値）。実際の通信経路・正確な転送速度・電池消費は未計測。

次は端末での残りの確認事項:

- Pixel 9aで保存済み音声がアプリ再起動後もオフライン再生できる。
- 変更版の音声を再送すると更新される。
- 転送中断/容量不足/受信画面を離れる操作で、前の音声が残り再送できる。
- スマホのBluetoothを切り、Watchのネット接続も切った状態で、Watchに接続したイヤホンから再生できる。
- イヤホン切断・他の音声/通話によるフォーカス喪失時の一時停止を、条件を揃えて確認する。
- Watch再起動後の一覧と再生位置、丸形画面の欠け・文字サイズ・操作性を確認する。
- 実測した転送時間と電池消費を記録する。

### 0.2.1 操作改善の確認

- Ownerからイヤホンで聴けているとの確認を受けた。上記0.2.0での聴感未確認は解消したが、全通信OFFの試験とは区別する。
- スマホversionCode 8 / Watch 9を共通署名で作成し、両実機へ上書き更新した。Releaseビルドはいずれも警告0・エラー0。WatchのARM32同梱を確認。アンインストール・データ消去は行っていない。
- 保存/転送・同期対象の永続化/ライブラリ分離/OFF時の非削除・v1/v2ヘッダー・画像容量と整理範囲の自動検証22件がDebug/Releaseで成功。
- Watchで更新前の1曲が残り、音符カードからプレイヤーを開けた。シークバーで約118秒へ変更し、更新後も同じ位置を復元できた。検証後は元の先頭へ戻した。音量スライダーで11→10へ変更でき、元の11へ戻した。丸形画面の主操作が下端に寄らない配置へ調整した。
- 0.2.1確認時はイヤホン未接続で、誤って本体スピーカーから流れず接続案内を表示した。対象アプリのクラッシュは観測していない。旧版での聴感確認と区別し、この更新での聴感・電池持ちは未計測。
- スマホ更新後の画面は端末ロック中。同期対象チェックの端末再起動後の表示、実画像付きv2転送はロック解除後に確認する。これらの実機成功はまだ主張しない。

## 参考

- [Wearable Data Layer](https://developer.android.com/training/wearables/data/overview)
- [ChannelClient](https://developers.google.com/android/reference/com/google/android/gms/wearable/ChannelClient)
- [Background audio](https://developer.android.com/media/platform/mediaplayer/background)
- [App-specific storage](https://developer.android.com/training/data-storage/app-specific)
- [Microsoft Wearable bindings](https://www.nuget.org/packages/Xamarin.GooglePlayServices.Wearable/119.0.0.3)
