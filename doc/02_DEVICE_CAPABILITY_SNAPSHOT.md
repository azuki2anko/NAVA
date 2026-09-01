# RX-V4A 実機Capabilityスナップショット

確認日: 2026-08-29

確認方法: 同一LANからYamaha Extended Control APIの読み取り専用エンドポイントを使用

秘匿方針: IPアドレス、MACアドレス、機器IDは本書に記載しない

## 1. 確認結果

| 項目 | 値 |
|---|---|
| モデル | RX-V4A |
| Yamaha Extended Control API | 2.15 |
| システム／ファームウェア表示 | 1.67 |
| Zone | `main`, `zone2` |
| Main Scene数 | 4 |
| Tuner | AM / FM、共通40プリセット |
| Zone2の性格 | Zone B |

この値は実装時の固定前提ではありません。接続のたびに `getDeviceInfo` と `getFeatures` を取得し、Capability駆動で画面・検証・API公開範囲を組み立ててください。

## 2. Systemで確認した機能

- `wired_lan`
- `wireless_lan`
- `network_standby`
- `network_standby_auto`
- `bluetooth_standby`
- `bluetooth_tx_setting`
- `bluetooth_tx_connectivity_type`
- `dfs_option`
- `headphone`
- `zone_b_volume_sync`
- `hdmi_out_1`
- `airplay`
- `disklavier_settings`
- `background_download`
- `remote_info`
- `network_reboot`
- `system_reboot`
- `name_text_avr`
- `hdmi_standby_through`
- `analytics`

## 3. Main Zoneで確認した機能

- `power`
- `sleep`
- `volume`
- `mute`
- `sound_program`
- `pure_direct`
- `enhancer`
- `tone_control`
- `dialogue_level`
- `subwoofer_volume`
- `signal_info`
- `prepare_input_change`
- `link_control`
- `link_audio_delay`
- `scene`
- `contents_display`
- `cursor`
- `menu`
- `actual_volume`
- `surr_decoder_type`
- `extra_bass`
- `adaptive_drc`

## 4. Main Zoneで確認した入力

- ネットワーク／配信: `spotify`, `qobuz`, `tidal`, `deezer`, `amazon_music`, `alexa`, `airplay`
- MusicCast／サーバー: `mc_link`, `server`, `net_radio`
- ローカル／無線: `bluetooth`, `usb`, `tuner`
- HDMI／TV: `hdmi1`, `hdmi2`, `hdmi3`, `hdmi4`, `tv`
- アナログ／その他: `audio1`, `audio2`, `audio3`, `audio4`, `audio5`

Alexaは実機Capabilityに存在しますが、本アプリのスコープ外です。

## 5. Zone2で確認した機能

- `power`
- `volume`
- `mute`
- `prepare_input_change`
- `actual_volume`

Zone2はMain Zoneと同等ではありません。共通のZoneモデルを用いつつ、操作の表示と受け付けは各ZoneのCapabilityに限定してください。

## 6. 実装上の原則

- Capabilityにないコントロールは非表示、または理由付きで無効化する。
- 入力名、Scene数、プリセット数、音量範囲・刻み値を固定値にしない。
- APIレスポンスの未知フィールドは無害に無視し、既知フィールドを厳密に検証する。
- 通信失敗、Yamahaのレスポンスコード、タイムアウト、キャンセルを区別する。
- イベント通知に加えて定期的な再同期を持ち、取りこぼしから回復できるようにする。
- 公開APIで不足が確認されるまで、未文書化エンドポイントへ進まない。

## 7. 安全境界

次の操作はUIやAPIに実装できても、実機での自動テスト対象にしません。

- ネットワーク設定の変更
- ネットワーク／システム再起動
- 初期化
- ファームウェア更新やバックグラウンドダウンロードに関する状態変更
- 接続を失う可能性がある設定変更

これらは警告と明示確認を必須にし、外部APIでは既定で無効にします。
