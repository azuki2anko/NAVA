# 公開版とプライベート版の開発方針

## リポジトリ構成

公開版とプライベート版は、同じGitリポジトリ内のブランチではなく、別々のリポジトリで管理する。

- 公開リポジトリ: 汎用機能の正本。GitHubで公開する。
- 非公開リポジトリ: 公開リポジトリを`upstream`として取り込み、個人環境固有の変更だけを追加する。

公開リポジトリにprivateブランチを作らない。削除済みファイルや過去コミットもGit履歴から取得できるため、一度でも秘密情報を公開リポジトリへコミットしない。

## 公開版へ含めるもの

- Capability駆動のYamaha Extended Control APIクライアント
- WPF、タスクトレイ、ミニ電源トグル、localhost REST API
- SSDP、Windows近隣キャッシュ、確認付きping探索
- 汎用アクティビティ、Power-on blocker、登録済みアクションの拡張基盤
- F13～F24グローバルホットキーの汎用実装
- モックテスト、公開可能な要件・設計・API文書
- 実機識別情報を除去したCapability情報

## プライベート版だけに含めるもの

- 実IPアドレス、MACアドレス、機器ID、読み取り／操作トークン
- `%LocalAppData%\RXV4A Manager`以下の設定とログ
- 個人用の入力名、MacroButton番号、ホットキー割り当て、活動プロファイル
- `tv-recording`、`spotify`、`call`、`vrchat`の既定プロファイル
- Voicemeeter Remote API、互換ホットキー、Virtual Desktop Monitor例外
- 署名鍵、証明書、GitHubやLAN公開用の資格情報
- 公開前の実験機能、個人環境にしか存在しない連携
- 未文書化APIの調査記録。使用には要件で定めた事前承認が必要。

## 変更の流れ

1. 汎用的な修正は公開版で実装し、ビルド・テスト・公開前監査を行う。
2. プライベート版で公開リポジトリの`main`を取得し、マージする。
3. 個人環境固有の修正は非公開リポジトリだけへコミットする。
4. privateからpublicへ変更を戻す場合は、秘密情報や個人固有値を除いた新しいコミットとして作り直す。

非公開リポジトリ側の例:

```powershell
git remote add upstream https://github.com/<owner>/rxv4a-manager.git
git fetch upstream
git merge upstream/main
```

## 公開前チェック

- `dotnet build RxV4A.Manager.sln -c Release`
- `dotnet test RxV4A.Manager.sln -c Release`
- `dotnet format RxV4A.Manager.sln --verify-no-changes`
- `bin/`、`obj/`、`artifacts/`、設定、ログ、private overlayが追跡対象にないこと
- 実IP、MAC、機器ID、トークン、鍵、個人パスが差分とGit履歴にないこと
- READMEと実装状況が一致していること
- 配布バイナリへ署名する場合、署名鍵をGitHubへ保存しないこと

## private overlayの一時配置

公開版の作業ツリー内で一時的にprivateファイルを扱う必要がある場合は、`.private/`または`private/`を使用する。これらは`.gitignore`で除外される。ただし、長期運用では公開版とは別ディレクトリの非公開リポジトリを使用する。
