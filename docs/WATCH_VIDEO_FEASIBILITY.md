# Pixel Watchでの動画再生：成立条件の調査

調査日: 2026-09-23 / ORC-20260923-005。対象: Reader0.2.5・Watch0.2.1（a9fddb9）。調査結果と実装候補であり、動画機能の採用決定・実装・インストールではない。

## 結論

現時点で「端末の仕様上、絶対に不可能」とする根拠はない。ただし、Pixel Watch5で目的の動画を再生できるデコーダーが提供されているかは未確認。**実機のデコード・描画試験を先に行い、成功してからCoffeeEagleの動画転送・保存・画面を実装する**のが妥当。

Googleの[Pixel Watch5公式仕様](https://store.google.com/jp/product/pixel_watch_5_specs?hl=ja)ではWear OS7、64GBストレージを確認できるが、この仕様表だけから動画コーデックの対応や連続再生時間は判断できない。Androidの[対応形式表](https://developer.android.com/media/platform/supported-formats)も、スマホ・タブレット以外では対応が異なると明記している。SoCの名称やMP4という拡張子だけで再生可能と判定しない。

## 代表MP4の実測

Owner指定の動画を元の保存場所で読み取りのみ調査。メディアの複製・変更・変換はしていない。私的なファイルID・保存場所・内容はこの資料に記載しない。

| 項目 | 確認結果 |
| --- | --- |
| コンテナ・サイズ | MP4、31,644,520 bytes（30.18MiB、アプリ表示30.2MB） |
| 長さ | 約179.292秒（2分59秒） |
| 映像 | H.264/AVC Baseline、Level3.0、448×656、24fps |
| 映像の平均ビットレート | 約1.206Mbps |
| 音声 | AAC-LC、44.1kHz、2ch、約192kbps |
| 検査方法 | WindowsメディアプロパティとMP4 moov内のsample entry・avcC・esdsを照合。avc1/profile_idc66/level_idc30、mp4a/AudioSpecificConfig1210（audioObjectType2）。Drive上のサイズも一致 |

まず原動画をそのまま試せる形式候補であり、変換を必須とは断定しない。端末の対応外・処理能力不足が判明した場合だけ、別の派生コピーを検討する。ファイルの一般的な破損検査やWatchでの再生成功を、このメタデータ検査から推定しない。

## クリアする必要がある条件

| 条件 | 合格とする証拠 | 現在の状態 |
| --- | --- | --- |
| 端末・アプリ互換性 | 実機のモデル、OS/API、ABIを採取し、同じ署名のWatchアプリが起動する | 既存Wear APKはarm/arm64/x64を含む。今回ADB接続は0台で、Pixel Watch5実機の情報は未採取 |
| 映像・音声のデコード | 元動画の完全なMediaFormatに対応するデコーダーを取得し、実際に映像と音声を最後まで出力できる | 未確認。形式一覧に載るだけでは性能の合格にならない |
| 動画を表示する画面 | 描画Surface、縦横比を保つ縮小、再生/停止/シーク、戻る・再開が動く | Watch側に未実装。スマホ側のVideoPlayerActivityを出発点にできる |
| 転送・保存 | 動画を選択でき、受信後に長さ・SHA256が一致し、途中中断で既存データを壊さない | 音声用の基盤は存在。現在MP4は送信対象・受信検証の両方で拒否される |
| 容量 | コピーと更新用一時領域を確保でき、上限超過を事前表示する | 代表動画自体は約30.2MiB。既存基盤なら新規コピー分に加え16MiBの空き余裕が必要。更新中は旧コピーも保持する |
| 音声出力・画面状態 | Bluetoothイヤホン等で音声を出し、切断・画面離脱時の一時停止と再開が動く | 現在の音声版の制御を参考にできる。動画のライフサイクル制御は追加が必要 |
| 実用性 | コマ落ち・音ずれ・操作遅延・発熱・電池消費を実測し、利用時間の希望を満たす | 未測定。公称の通常使用時間を動画連続再生時間に置き換えない |

デコーダー検査には[MediaCodecList.findDecoderForFormat](https://developer.android.com/reference/android/media/MediaCodecList#findDecoderForFormat(android.media.MediaFormat))、[VideoCapabilities.areSizeAndRateSupported](https://developer.android.com/reference/android/media/MediaCodecInfo.VideoCapabilities#areSizeAndRateSupported(int,int,double))等を使う。プロファイル/レベル、サイズ/フレームレート、音声も確認する。ハードウェア支援の有無は[MediaCodecInfo](https://developer.android.com/reference/android/media/MediaCodecInfo#isHardwareAccelerated())で記録できるが、メーカー申告値であり持続性能の実測に代わらない。ソフトウェアデコーダーしかなくても即不可能ではないが、実用性の試験が必要。

Media3を導入すればデコーダー不足が必ず解消するわけではない。[ExoPlayerは標準で端末のデコーダーを使う](https://developer.android.com/media/media3/exoplayer/supported-formats)。独自ソフトウェアデコーダーの追加は別のコスト・配布・保守判断になる。

## CoffeeEagleで必要な変更と再利用できる部分

| 場所 | 現状 | 動画対応の候補 |
| --- | --- | --- |
| Reader/Services/OfflineAudioService.cs | MediaKind.Audioと音声拡張子だけを許可 | 動画用の明示選択・準備・保存を追加 |
| Offline/OfflineTrack.cs、AudioWire.cs | 音声拡張子限定、audio v1/v2契約 | 旧WatchへMP4を送らない能力交渉と動画用契約・保存領域を別途設計 |
| WearTransport/WearAudioTransfer.cs | ChannelClientでストリーム転送し、検証済み応答を確認 | 通信・中断・ハッシュ検証の方式を再利用可能。音声契約へMP4だけ足す変更では不十分 |
| Wear/AudioPlaybackService.cs | MediaPlayerの音声再生。映像Surfaceなし | Watch用動画Activityを追加し、前景再生・音声競合・離脱時停止を管理 |
| Reader/Platforms/Android/VideoPlayerActivity.cs | VideoView、シーク、再開を実装済み | 最小実験の参考にできる。丸い画面のレイアウト・小さな操作部は作り直す |

既存の[ChannelClient](https://developer.android.com/training/wearables/data/client-types)は大きなファイルのストリーム転送に使える。現在の転送は近接端末を要求し、30分でタイムアウトする。回線速度は未測定のため転送所要時間は約束しない。転送後のローカル再生なら、再生のたびにスマホやDriveとの接続を必要としない構成にできる。

推奨する初期構成は「スマホで選ぶ→Watchへ完全保存→Watchで前景再生」。Googleの[Wear OSメディア設計](https://developer.android.com/media/implement/surfaces/wear-os)も通信・電力面から保存済みコンテンツを重視している。初期版から常時ストリーミングを必要条件にしない。

丸い表示領域には全体が入るよう余白を許容し、勝手に映像を切り取らない。動画再生中の画面維持は限定的に扱い、停止/離脱で解除する。[Ambient/画面維持の公式説明](https://developer.android.com/training/wearables/always-on)では常時Interactiveを維持する電池負荷が指摘されている。音声版のバックグラウンド再生を、そのまま動画描画へ流用しない。

## 最小の検証順序と判定案

以下の数値は今回提案する試験基準であり、Googleの保証値や既に合格した結果ではない。

1. **能力確認**: 実機モデル/OS/ABI、codec一覧、AVC Baseline L3.0・448×656・24fps、AAC-LC/44.1kHz/2chの対応を確認する。対象Watchが無線デバッグへ接続されていることが必要。
2. **単体再生**: 他のデータを触らない最小の前景動画プレイヤーで代表MP4をローカル再生。初回フレーム3秒以内、全179秒をエラーなしで完走、停止/再開/シークが動くことを確認する。コマ落ちは1%未満、持続的な音ずれは100ms以内を目標に実測する。
3. **負荷が高い場合のみ軽量化**: 原本を残して、H.264 Baseline・長辺320〜480px・元の24fps・映像400〜800kbps・AAC-LC64〜96kbpsを試す。縦横比とデコーダーの寸法整列条件を維持する。これは候補設定であり、端末の受理を保証しない。必要ならさらにfpsを落として比較する。
4. **持続性**: 同じ明るさ・音声出力で10分以上繰り返し再生し、電池減少・熱状態・ドロップを記録する。熱保護による停止や持続的な速度低下がないこと。望む連続視聴時間との適合は測定後に判断する。
5. **製品統合**: 端末単体の試験に合格してから、動画用の選択・転送・保存・画面をCoffeeEagleへ統合。完全受信・中断・再送・空き容量不足・旧音声版との互換・既存音声保持を試験する。
6. **Watch単独確認**: 転送完了後、スマホ/ネット接続に依存しない再生を確認。イヤホン接続を維持してテストし、必要な通信設定変更は復元する。

元動画で成功すれば変換不要。軽量コピーだけ成功すれば「Watch用コピーを生成する条件付き対応」。対応するデコーダーがなく標準APIで描画できなければ、通常のプレイヤー追加ではクリアできず、別コーデック/独自デコーダーの検討が必要になる。映像が出ても電池・熱・操作性が基準を満たさなければ、短時間用途への限定など別の製品判断が必要。

## 今回の到達点

公式情報・コード・代表ファイルの形式調査は完了。WatchがADB未接続のため、端末のcodec取得と再生/電力実測は未実施。アプリ・APK・保存済み音声・元動画に変更はない。次の技術的判断材料は**実機で元動画を最後まで描画できるか**であり、設定変更だけで現行Watch版が動画対応するわけではない。
