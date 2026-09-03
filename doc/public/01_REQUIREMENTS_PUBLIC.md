# Yamaha AV Manager 公開版要件定義書

版: 2.1

更新日: 2026-09-03

対象: Windows 11 x64 / Yamaha Extended Control対応アンプ（実機確認: RX-V4A）

## 1. 目的

同一LAN上のYamaha Extended Control対応アンプを、Windows 11のGUI、タスクトレイ、グローバルホットキー、型付きlocalhost APIから安全に管理する公開可能なデスクトップアプリを提供する。PC用アンプとして使用頻度の高いMain Zone操作を優先する。

公開版は、個人環境固有の接続先、識別子、マクロ番号、外部アプリ名、秘密情報を含まない。実機Capabilityに応じて利用可能な機能だけを提示する。実機で動作確認する機種はRX-V4Aだけとし、ほかの機種はYamaha公式仕様に基づく互換動作として扱い、動作保証しない。

## 2. 対象範囲

### PUB-SCOPE-01 対象

- Windows 11 x64、.NET 10 LTS、WPF
- タスクトレイ常駐アプリ。Windowsサービスには分離しない。
- Yamaha Extended Control APIによる対応アンプの検出、状態取得、制御
- `getFeatures`を単一のCapability情報源とする画面・API・検証
- シンプルなメイン画面、最前面ミニ電源トグル、タスクトレイ操作
- localhost REST API、OpenAPI、将来の状態イベントAPI
- Ctrl／Alt／Shift + F13～F24のグローバルホットキー
- 構成可能な汎用アクティビティ、Power-on blocker、登録済みアクションの拡張基盤
- JSON設定、構造化ログ、タイムアウト、自動再接続、モックテスト
- 公開済みYamaha APIのCapability駆動による段階的な網羅

### PUB-SCOPE-02 対象外

- Amazon Alexa連携
- PVE、Ubuntu、自宅サーバーへの常駐
- Windowsサービスとしてのログオン前常駐
- Windows 10、32bit Windows
- アンプの認証なしHTTP APIのインターネット直接公開
- プログラマブルキーボード固有SDK、HID、MIDIへの直接対応
- 音楽サービスの認証情報保存や公式クラウド停止の回避
- 通話、VR、音楽、録画等の外部アプリ自体の制御やプロセス監視
- ファームウェアの配布・改変

## 3. 基本方針

### PUB-PR-01 Capability駆動

- `getDeviceInfo`、`getFeatures`、`getAdvancedFeatures`、Zone状態を型付きで取得する。
- モデル名の固定許可リストではなく、Main Zoneと広告されたCapabilityから互換性を判定する。
- 入力、機能、値域、SCENE数、Zone差を固定値にしない。
- 実機が広告しない機能は呼び出さず、通常画面に表示しない。
- 読み取り専用、操作可能、予約済み、サービス終了、実機非対応を区別する。
- 実機識別子は内部照合だけに使い、GUI、共有ログ、外部APIではマスクする。

### PUB-PR-02 安全なAPI境界

- 外部APIはYamaha APIの汎用プロキシにしない。
- 任意URL、任意コマンド、任意キー列、任意Yamahaエンドポイントを受け付けない。
- すべての変更操作は型、値域、Capability、権限を検証する。
- 未文書化APIは公開APIの不足を証拠付きで確認し、ユーザーの明示承認を得るまで使用しない。

### PUB-PR-03 実機確認

- 実機テストと動作保証の対象はRX-V4Aだけとする。
- ほかの機種についてはYamaha公式API仕様への準拠とモックテストだけを表明し、実機確認済みとは記載しない。
- 新しい実機への最初の接続確認は読み取り専用とする。
- 電源、入力、音量等をその実機で初めて変更する直前に、操作内容を示して確認を得る。
- ネットワーク設定、再起動、初期化、ファームウェア関連は、都度の明示確認なしに実行しない。

## 4. 機能要件

### PUB-FR-100 機器検出・接続

- PUB-FR-101: 保存済みまたは手動指定ホストを`getDeviceInfo`で最初に検証する。
- PUB-FR-102: SSDP/UPnPを、選択された有効な実NICへバインドして探索する。
- PUB-FR-103: Windows近隣キャッシュのprivate IPv4候補を読み取り専用で検証する。
- PUB-FR-104: `/24`以上の小さい範囲だけ自動ping探索する。
- PUB-FR-105: `/16`～`/23`は候補数と読取内容を表示し、明示承認後だけping探索する。
- PUB-FR-106: `/16`未満の広い範囲は総当たりせず、手動接続を案内する。
- PUB-FR-107: Yamaha応答コード成功かつ、`getFeatures`にMain Zone、入力、基本操作のいずれかが広告された互換機器であることを検証する。
- PUB-FR-108: IP変更後も内部機器IDで同一機器を照合する。
- PUB-FR-109: 接続、切断、再接続、探索確認待ち、非対応を区別して表示する。
- PUB-FR-110: 確認待ち中も保存済み／手動ホストへの読み取り専用再接続を継続する。
- PUB-FR-111: SSDP無応答だけでアンプ不在と確定しない。

### PUB-FR-200 状態と基本制御

- PUB-FR-201: 機器情報、Capability、Main Zone状態を取得する。
- PUB-FR-202: Main Zoneの`on`と`standby`を個別指定し、状態反転型トグルAPIにしない。
- PUB-FR-203: 入力、音量、ミュート、音場プログラム、3D Surround、Direct、Pure Direct、Enhancer、トーン、EQ、バランスをCapabilityに応じて表示・操作する。
- PUB-FR-207: PC用アンプの日常操作として、電源、映像／音声入力切替、音量、ミュート、音声処理を最優先で実装する。
- PUB-FR-204: コマンドを直列化し、完了後に状態を読み戻す。
- PUB-FR-205: 物理リモコンやMusicCastアプリによる変更を同じ状態へ収束させる。
- PUB-FR-206: 低頻度ポーリングと手動更新を提供する。

### PUB-FR-300 公開Yamaha APIの段階的網羅

実機Capabilityに応じ、次の公開API領域を段階的に実装する。日常操作以外は詳細画面へ隔離する。

- System: 機器・バージョン・ネットワーク・Bluetooth・名称・HDMI・スタンバイ等
- Zone: 電源、入力、音量、ミュート、音場、音声処理、信号情報、SCENE等
- Tuner: 将来候補。利用場面とUIを再検討するまで実装を保留する。
- Network/USB: 再生情報、再生制御、リスト、プリセット、検索、キュー
- CD、Clock: 実機が広告する場合だけ型付きで提供
- MusicCast Link / Distribution: Advanced画面へ隔離し、誤操作を防止

値は仕様書の固定値だけでなく、実機が返す選択肢と`range_step`で検証する。ネットワーク変更、MACフィルター、再起動、配信再構成は高影響操作とする。

### PUB-FR-400 GUI・タスクトレイ・ミニ操作

- PUB-FR-401: 起動時はメイン画面を表示せず、タスクトレイへ収納する。
- PUB-FR-402: メイン画面は状態と日常操作を簡潔にし、網羅機能を詳細画面へ分離する。
- PUB-FR-403: タスクトレイから状態更新、電源、メイン画面、ミニ操作、終了へアクセスできる。
- PUB-FR-404: ミニ電源トグルはタイトルバーなし、最前面、タスクバー非表示とする。
- PUB-FR-405: ミニトグルはON/OFF状態をカプセル型スイッチ自体で示す。
- PUB-FR-406: ミニ画面はマウスで拡大縮小でき、安全な最小・最大サイズを持つ。
- PUB-FR-407: つかむバーとトグルを同じ倍率で拡大縮小する。
- PUB-FR-408: 位置とサイズを保存し、実際の仮想画面座標・解像度が変わった場合は既定位置へ戻す。
- PUB-FR-409: 非対応または未接続の操作は無効化し、理由を状態またはツールチップで確認できる。
- PUB-FR-410: 高DPI、キーボード操作、日本語UIへ対応する。

### PUB-FR-500 ローカルAPI

- PUB-FR-501: 既定URLを`http://127.0.0.1:55274/api/v1`とする。
- PUB-FR-502: 状態、Capability、Main Zone操作を型付きJSON APIで提供する。
- PUB-FR-503: OpenAPI文書と契約テストを提供する。
- PUB-FR-504: 状態変更は冪等なPUTまたは目的が限定されたPOSTで表現する。
- PUB-FR-505: 既定ではlocalhostだけにバインドする。
- PUB-FR-506: 任意のLAN公開を有効化する場合、読み取り用と操作用トークンを分離する。
- PUB-FR-507: LAN公開時は明示CORS許可元とWindows Firewallの説明を必要とする。
- PUB-FR-508: エラーは非対応、範囲外、切断、タイムアウト、競合、確認拒否を機械可読コードで区別する。

### PUB-FR-600 グローバルホットキー

- PUB-FR-601: `RegisterHotKey`と`MOD_NOREPEAT`を使用する。
- PUB-FR-602: Ctrl、Alt、Shiftの任意組み合わせとF13～F24を設定できる。
- PUB-FR-603: Windowsキーと低レベルキーボードフックを標準方式にしない。
- PUB-FR-604: タスクトレイ常駐中も受信し、終了時に全登録を解放する。
- PUB-FR-605: 競合した割り当てだけを無効化し、アプリ全体は継続する。
- PUB-FR-606: 設定変更を再起動なしで再登録する。
- PUB-FR-607: 公開版の初期割り当てはCtrl+Alt+F13=ON、Ctrl+Alt+F14=Standbyとし、F15～F24は未割り当てとする。
- PUB-FR-608: ホットキー操作にもCapability、冪等性、Power-on blocker、安全権限を適用する。

### PUB-FR-700 汎用アクティビティとPower-on blocker

- PUB-FR-701: 複数の登録済み操作を順序付きアクティビティとして定義できる。
- PUB-FR-702: アクティビティは現在状態を確認し、不要な操作を送らない。
- PUB-FR-703: 複数のPower-on blockerを同時に有効化でき、全解除まで自動ONを拒否する。
- PUB-FR-704: blocker解除だけではアンプを自動ONにしない。
- PUB-FR-705: blocker中に外部操作でアンプがONになっても自動OFFせず、警告だけを表示する。
- PUB-FR-706: 安全側のStandbyと補助アクションに部分失敗が生じても、可能な安全操作を継続する。
- PUB-FR-707: 公開版は個人用のアクティビティ名、コンテキスト名、マクロ割り当てを既定登録しない。
- PUB-FR-708: 外部APIは事前登録した論理アクションIDだけを列挙・実行できる。

### PUB-FR-800 設定・ログ・再接続

- PUB-FR-801: 設定を`%LocalAppData%\Yamaha AV Manager\settings.json`へ保存する。
- PUB-FR-802: 構造化JSON Linesログを日次管理する。
- PUB-FR-803: IP、MAC、機器ID、トークン、個人識別子を共有ログへ出さない。
- PUB-FR-804: 接続・要求タイムアウト、キャンセル、低頻度ポーリング、自動再接続を実装する。
- PUB-FR-805: 設定やログの破損で受信機制御プロセス全体を停止させない。
- PUB-FR-806: スタートアップ登録は現在の配布バイナリを対象とし、ユーザーが解除できる。

## 5. 非機能要件

- PUB-NFR-01: LAN正常時の一般操作は原則1秒以内に結果を表示する。
- PUB-NFR-02: 外部状態変更は原則2秒以内に反映する。
- PUB-NFR-03: 対応アンプ不在、IP変更、再起動、NIC変更でアプリが停止しない。
- PUB-NFR-04: 通常利用で外部インターネット通信やテレメトリを必要としない。
- PUB-NFR-05: self-contained Windows 11 x64配布を作成できる。
- PUB-NFR-06: Core／Hostのモック単体テストとlocalhost API契約テストを継続する。

## 6. セキュリティ要件

- PUB-SEC-01: アンプの認証なしHTTP APIをインターネットへ公開しない。
- PUB-SEC-02: 任意URL、任意コマンド、任意キー列、任意Yamaha APIを外部入力から実行しない。
- PUB-SEC-03: LAN公開トークン、署名鍵、証明書をソースやGit履歴へ保存しない。
- PUB-SEC-04: 音量上限を設定でき、Capabilityの値域外操作を拒否する。
- PUB-SEC-05: 未文書化APIは既定無効とし、公開API不足の証拠とユーザー承認を必要とする。
- PUB-SEC-06: 実機変更のスモークテストは対象操作を明示して承認後に行う。

## 7. 受入条件

- PUB-AC-01: 保存済み、SSDP、近隣キャッシュ、確認付きping、手動指定の順で安全に接続できる。
- PUB-AC-02: `getDeviceInfo`、`getFeatures`、Main Zone状態を取得できる。
- PUB-AC-03: ONとStandbyを状態逆転なしでGUI、トレイ、API、ホットキーから実行できる。
- PUB-AC-04: Capabilityにない操作を表示・送信しない。
- PUB-AC-05: 切断、IP変更、一時タイムアウト後に自動復旧する。
- PUB-AC-06: メイン画面を隠したままタスクトレイ、ミニトグル、ホットキーが動作する。
- PUB-AC-07: OpenAPIと実装の契約テストが通る。
- PUB-AC-08: Power-on blockerの重複、全解除、自動ON禁止、外部ON警告だけの規則がテストされる。
- PUB-AC-09: localhost以外から既定APIへ到達できない。
- PUB-AC-10: 機器識別子、MAC、トークンがソース、API応答、共有ログへ露出しない。
- PUB-AC-11: ネットワーク設定、再起動等を確認なしに即実行できない。
- PUB-AC-12: Ctrl／Alt／Shift + F13～F24を個別登録し、競合と長押しを安全に処理できる。

## 8. 確定事項

1. 公開版はWindows 11 x64 / .NET 10 LTS / WPFとする。
2. タスクトレイ常駐とし、Windowsサービス、自宅サーバー、Alexaを追加しない。
3. localhost APIを既定とし、LAN公開は明示設定時だけとする。
4. `getFeatures`をCapabilityの単一情報源とする。
5. 公開版には個人環境固有の既定アクション、接続先、マクロ、識別子を含めない。
6. 未文書化APIはユーザー承認まで実装・使用しない。
7. 実機確認済み機種はRX-V4Aだけとし、ほかの機種は公式仕様とCapabilityに基づく互換動作とする。
8. PC用アンプの日常操作を優先し、Tunerは当面保留する。

## 9. 参照資料

- Yamaha Extended Control API Specification (Basic / Advanced), Rev.2.00
- 実機識別情報を除去した[`../02_DEVICE_CAPABILITY_SNAPSHOT.md`](../02_DEVICE_CAPABILITY_SNAPSHOT.md)
- Microsoft .NET 10 LTS / WPF / RegisterHotKey documentation
