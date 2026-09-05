# コントリビューションガイド

Network AV Amp ControllerへのIssue、文書改善、テスト、コード変更を歓迎します。

## 開発環境

- Windows 11 x64
- .NET SDK（[`global.json`](global.json)に記載したバージョン）
- Git

```powershell
dotnet restore NetworkAVAmp.Controller.sln
dotnet build NetworkAVAmp.Controller.sln -c Release --no-restore
dotnet test NetworkAVAmp.Controller.sln -c Release --no-build --no-restore
dotnet format NetworkAVAmp.Controller.sln --verify-no-changes --no-restore
```

## 設計原則

- `getFeatures`をCapabilityの単一情報源にする。
- 入力、音場、値域、SCENE数、Zone差を機種名で固定しない。
- Yamaha公開仕様にある型付き操作だけを追加し、汎用APIプロキシを作らない。
- 外部APIから任意URL、任意コマンド、任意キー列を受け付けない。
- Capabilityにない機能を表示・送信しない。
- Core → Host → Desktopの依存方向を逆転させない。
- 公開版から`.private/`、`private/`、個人用実装を参照しない。

実機確認済み機種はRX-V4Aだけです。ほかの機種向け変更は、公式仕様とモックテストに基づく互換実装として提出してください。

## 実機テスト

実機テストは読み取り専用から始めてください。電源、入力、音量などを変更する前に、対象操作と想定結果を機器所有者へ示して同意を得てください。ネットワーク設定、再起動、初期化、ファームウェア関連は通常のPull Request検証対象にしません。

実機がなくても、HTTPハンドラーと`IDeviceManager`のモックによるテストを追加できます。未知フィールド、Capability不足、値域外、タイムアウト、Yamahaエラー応答も検討してください。

## 公開情報

コミット、テストデータ、Issue、ログには、実IPアドレス、MACアドレス、機器ID、トークン、鍵、個人パス、個人用アクション名を含めないでください。例示用IPが必要な場合はRFC 5737のTEST-NETアドレスを使用してください。

## Pull Request

変更目的を一つに絞り、関連するテストと文書を更新してください。レビュー前にRelease build、test、formatを実行し、実機確認の有無を明記してください。
