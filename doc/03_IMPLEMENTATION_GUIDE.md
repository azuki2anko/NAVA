# Yamaha AV Manager 公開版実装ガイド

正式要件は[`public/01_REQUIREMENTS_PUBLIC.md`](public/01_REQUIREMENTS_PUBLIC.md)であり、本書は実装構造の補助説明です。

## ソリューション境界

- `RxV4A.Core`: Yamaha型、Capability、探索、Power-on blockerの汎用状態管理。HostやDesktopを参照しない。
- `RxV4A.Host`: 設定、ログ、接続管理、アクティビティ調停、登録済みアクション契約、localhost API。
- `RxV4A.Desktop`: WPF、タスクトレイ、ミニ操作、`RegisterHotKey`。
- テスト: Core単体テストとHost／localhost API契約テスト。

参照方向はDesktop → Host → Coreとし、公開プロジェクトからprivate領域を参照しない。

## 汎用制御

- 接続可否はモデル名の固定比較ではなく、`getDeviceInfo`成功後の`getFeatures`にMain Zone、入力、基本操作が存在するかで判定する。
- 実機確認済みはRX-V4Aだけとし、ほかの機種は公式Yamaha Extended Control仕様に基づく互換動作として表示する。
- `getAdvancedFeatures`は日常操作の接続要件にせず、取得不能でもBasic APIによる操作を継続する。
- Main Zoneの日常操作は電源、入力、音量、ミュート、音場、3D Surround、Direct、Pure Direct、Enhancer、トーン、EQ、バランスを優先する。
- 選択値はCapabilityの一覧、数値は`range_step`で検証し、不完全なCapabilityから変更要求を組み立てない。
- アクティビティIDとPower-on blocker IDは設定で登録し、公開版の初期値は空にする。
- アクティビティは入力またはSCENEのいずれか一方だけを持ち、Capabilityで検証する。
- blockerは集合として重複可能に扱い、全解除まで自動ONを拒否する。
- blocker解除だけでは自動ONせず、blocker中の外部ONには警告だけを出す。
- 補助処理は`IRegisteredAction`として事前登録し、任意コマンドや任意キー列を公開境界から受け付けない。
- 補助処理の失敗を段階結果へ残し、安全側のStandbyは可能な限り継続する。

## ホットキーとAPI

- ホットキーはCtrl／Alt／Shift + F13～F24だけを受け付け、`RegisterHotKey`と`MOD_NOREPEAT`を使う。
- 初期割り当てはF13のON、F14のStandbyだけとする。
- APIは登録済みアクティビティ、blocker、アクションだけを列挙・操作する。
- APIは既定で`127.0.0.1`にだけバインドし、Yamaha APIの汎用プロキシにしない。
- Tuner／ラジオ機能は利用場面とUIが確定するまで実装を保留する。

## 検証

各マイルストーンでRelease build、test、format、公開情報監査を行う。RX-V4A以外を実機確認済みと表明しない。実機検証は読み取り専用から始め、状態変更前にユーザー確認を得る。
