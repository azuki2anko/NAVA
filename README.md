# RX-V4A Manager

Windows 11 x64 / .NET 10 LTS / WPFで動作する、Yamaha Extended Control対応アンプ向けタスクトレイ常駐アプリです。正式な要件基準は [`doc/public/01_REQUIREMENTS_PUBLIC.md`](doc/public/01_REQUIREMENTS_PUBLIC.md) です。

本プロジェクトは非公式のコミュニティプロジェクトであり、ヤマハ株式会社による提供・保証・承認を受けたものではありません。

実装はYamaha Extended Control API Specification (Basic / Advanced) Rev.2.00に基づきます。実機で動作確認している機種はRX-V4Aだけです。ほかの機種は`getFeatures`が広告する機能と値域に従って互換動作を試みますが、実機動作は保証しません。

## 現在の実装範囲

- 複数IPv4インターフェースからのSSDP探索、Windows近隣キャッシュ、確認付きping探索、手動ホスト指定
- `getDeviceInfo`、`getFeatures`、`getAdvancedFeatures`、Main Zone `getStatus`
- `getFeatures`を単一情報源にしたMain Zone ON / Standby
- 入力、音量、ミュート、音場プログラム、3D Surround、Direct、Pure Direct、Enhancer、トーン、EQ、バランスのCapability駆動操作
- 接続状態とPC用アンプの日常操作をまとめたWPF画面
- 状態確認・更新・電源操作を行うタスクトレイメニュー
- 最前面・リサイズ対応のミニ電源トグルと位置保存
- `http://127.0.0.1:55274/api/v1` の型付きREST API
- `http://127.0.0.1:55274/openapi/v1.json` のOpenAPI文書
- 構成可能な汎用アクティビティと複数Power-on blocker
- `IRegisteredAction`による、事前登録アクションだけを実行できる拡張契約
- `RegisterHotKey`と`MOD_NOREPEAT`によるCtrl／Alt／Shift + F13～F24
- Ctrl+Alt+F13=ON、Ctrl+Alt+F14=Standby。F15～F24は未割り当て
- JSON設定、日次JSON Linesログ、タイムアウト、低頻度ポーリング、自動再接続
- HTTPモック単体テストとループバックAPI契約テスト

公開版は個人用プロファイル、外部アプリ固有アダプター、個人用例外を登録しません。アクティビティ、blocker、登録済みアクションは設定または公開契約を通じて追加し、外部入力から任意URL、任意コマンド、任意キー列、任意Yamaha APIを実行する機能は提供しません。

## ビルドとテスト

```powershell
dotnet build RxV4A.Manager.sln -c Release
dotnet test RxV4A.Manager.sln -c Release
dotnet format RxV4A.Manager.sln --verify-no-changes
```

起動すると、保存済みの手動接続先がなければ読み取り専用の探索を開始します。

```powershell
dotnet run --project src/RxV4A.Desktop/RxV4A.Desktop.csproj -c Debug
```

GUI、トレイ、REST APIの電源操作は、接続済みかつCapabilityで許可された場合だけ有効です。REST APIは状態反転ではなく、`on`または`standby`を明示します。

```http
PUT /api/v1/zones/main/power
Content-Type: application/json

{ "power": "on" }
```

入力、音量、ミュート、音声処理も`/api/v1/zones/main`以下の型付きエンドポイントとして提供します。指定値は入力一覧、音場一覧、`range_step`に対して検証されます。Tuner／ラジオ機能は現時点では保留です。

実機で最初に状態を変更する前に、対象操作を明示して確認を得てください。

## プライバシーと公開境界

実行時設定と構造化ログはリポジトリ外の`%LocalAppData%\RXV4A Manager`に保存されます。Issueや共有ログへ設定ファイル、機器ID、MACアドレス、トークンを添付しないでください。

`.private/`、`private/`、`src/RxV4A.Private*`、`tests/RxV4A.Private*`は公開ソリューションから参照しません。公開・非公開開発の運用は [`doc/PUBLIC_PRIVATE_DEVELOPMENT.md`](doc/PUBLIC_PRIVATE_DEVELOPMENT.md)、現在の分離判断は [`doc/public/CODE_SEPARATION_PLAN.md`](doc/public/CODE_SEPARATION_PLAN.md) を参照してください。

## ライセンス

Copyright (c) 2026 anko。MIT Licenseで公開します。詳細は[`LICENSE`](LICENSE)を参照してください。
