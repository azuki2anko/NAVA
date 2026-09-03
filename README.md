# RX-V4A Manager

Windows 11からYamaha Extended Control対応アンプを操作する、Capability駆動のタスクトレイ常駐アプリです。リモコンの完全再現ではなく、PC用アンプとして使うMain Zoneの日常操作へ重点を置いています。

> [!IMPORTANT]
> 実機で動作確認している機種はRX-V4Aだけです。ほかの機種はYamaha Extended Control API Specification (Basic / Advanced) Rev.2.00と`getFeatures`に基づく互換動作であり、動作保証はありません。

本プロジェクトは非公式のコミュニティプロジェクトであり、ヤマハ株式会社による提供、承認、保証はありません。

## 主な機能

- 電源ON／Standby、映像・音声入力、音量、ミュート
- 音場プログラム、3D Surround、Direct、Pure Direct、Enhancer
- トーン、EQ、左右バランス
- SSDP、Windows近隣キャッシュ、確認付きping探索、手動接続
- シンプルなWPF画面、タスクトレイ、最前面ミニ電源トグル
- Ctrl／Alt／Shift + F13～F24のグローバルホットキー
- `127.0.0.1`限定の型付きREST APIとOpenAPI
- 汎用アクティビティ、Power-on blocker、登録済みアクション拡張契約
- JSON設定、構造化ログ、タイムアウト、ポーリング、自動再接続

機能、入力、音場、値域は`getFeatures`から取得します。機器が広告しない操作は画面へ表示せず、API要求も拒否します。Tuner／ラジオ機能は現在保留中です。

## 必要な環境

- Windows 11 x64
- PCとアンプが同じLANに接続されていること
- ネットワーク制御に対応したYamahaアンプ

通常利用で外部インターネット通信やテレメトリは必要ありません。

## 使い始める

GitHub Releasesの`win-x64`配布ZIPを展開し、`RXV4A.Manager.exe`を起動します。アプリはタスクトレイへ常駐し、読み取り専用の機器探索を開始します。詳しい初回接続、画面操作、ミニ画面、ホットキー、トラブル対処は[利用説明書](doc/USER_GUIDE.md)を参照してください。

ソースから起動する場合:

```powershell
dotnet restore RxV4A.Manager.sln
dotnet run --project src/RxV4A.Desktop/RxV4A.Desktop.csproj -c Debug
```

## localhost API

- API: `http://127.0.0.1:55274/api/v1`
- OpenAPI: `http://127.0.0.1:55274/openapi/v1.json`

APIは状態と目的を限定した型付き操作だけを提供します。任意のYamaha APIを転送するプロキシではありません。要求例は[localhost APIガイド](doc/API_GUIDE.md)を参照してください。

## 文書

- [利用説明書](doc/USER_GUIDE.md)
- [localhost APIガイド](doc/API_GUIDE.md)
- [公開版要件定義](doc/public/01_REQUIREMENTS_PUBLIC.md)
- [実装ガイド](doc/03_IMPLEMENTATION_GUIDE.md)
- [GitHub公開・リリースチェックリスト](doc/RELEASE_CHECKLIST.md)
- [変更履歴](CHANGELOG.md)
- [コントリビューションガイド](CONTRIBUTING.md)
- [セキュリティポリシー](SECURITY.md)

## ビルドと検証

```powershell
dotnet restore RxV4A.Manager.sln
dotnet build RxV4A.Manager.sln -c Release --no-restore
dotnet test RxV4A.Manager.sln -c Release --no-build --no-restore
dotnet format RxV4A.Manager.sln --verify-no-changes --no-restore
dotnet publish src/RxV4A.Desktop/RxV4A.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

GitHub Actionsでも同じRelease build、test、format、self-contained publishを検証します。

## プライバシーと安全性

実行時設定とログは`%LocalAppData%\RXV4A Manager`へ保存します。設定ファイルや無加工ログをIssueへ添付しないでください。IPアドレス、MACアドレス、機器ID、トークン、鍵、個人パスを公開しないでください。

公開版は外部入力から任意URL、任意コマンド、任意キー列、任意Yamaha APIを実行しません。実機の最初の確認は読み取り専用から始めてください。詳細は[セキュリティポリシー](SECURITY.md)を参照してください。

## ライセンス

Copyright (c) 2026 anko

[MIT License](LICENSE)
