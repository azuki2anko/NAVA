# 公開版コード分離計画

更新日: 2026-09-02

正式要件: [`01_REQUIREMENTS_PUBLIC.md`](01_REQUIREMENTS_PUBLIC.md)

## コード分類

| 領域 | 分類 | 公開版での扱い |
|---|---|---|
| Yamaha型付きクライアント、Capability、探索 | 残す | Coreに維持し、`getFeatures`を単一のCapability情報源にする |
| 接続管理、再接続、設定、構造化ログ | 残す | Hostに維持し、識別情報を外部応答・共有ログへ出さない |
| WPF、タスクトレイ、ミニ電源操作 | 残す | Desktopに維持し、Capability不足時は操作を無効化する |
| localhost REST API、OpenAPI | 残す | Hostに維持し、型付き操作だけを公開する |
| F13～F24グローバルホットキー | 汎用化 | ON／Standby以外は登録済み論理IDへ割り当てる。初期値はF13／F14のみ |
| アクティビティ、Power-on blocker | 汎用化 | 固定名をなくし、設定に登録された論理IDだけを扱う |
| 補助アクション | 汎用化 | `IRegisteredAction`と順序付き`ActionSequenceBinding`の背後へ隔離する |
| 外部アプリ固有アダプター、任意キー送出 | 公開版から外す | 公開ソース、DI、API、UI、テストから除去する |
| 個人用プロファイル、固定アクション名、個人用例外 | 公開版から外す | 公開初期値を空にし、公開文書とテストは汎用IDだけを使う |
| 実設定、ログ、機器識別子、資格情報 | 公開版から外す | リポジトリ外で管理し、Git追跡対象にしない |

## 単独ビルド構成

```text
RxV4A.Desktop
    ↓
RxV4A.Host
    ↓
RxV4A.Core
```

`RxV4A.Manager.sln`は公開3プロジェクトと公開テスト2プロジェクトだけを含む。private版は公開リポジトリをupstreamとして取り込み、必要な実装を`IRegisteredAction`として追加する。公開版からprivateプロジェクトへの参照は作らない。

## マイルストーン

1. 公開境界の確立: 固定プロファイルと外部アプリ固有コードを除去し、登録済みアクション契約へ置換する。
2. 汎用設定UI: アクティビティ、blocker、アクション割り当てを固定名なしで編集できる画面を追加する。
3. Yamaha公開APIの段階的拡張: Main Zone基本操作から、Capabilityが広告する領域へ型付きで広げる。
4. LAN公開の任意機能: 読み取り用／操作用トークン、CORS、Firewall案内を要件どおり実装する。
5. 配布監査: self-contained win-x64 publish、契約テスト、公開情報監査、README同期を完了する。

Git初期化、コミット、GitHub作成、push、releaseは、リポジトリ名、所有者、ライセンス、公開内容の確認後に行う。
