# GitHub公開・リリースチェックリスト

所有者: `azuki2anko`

推奨リポジトリ名: `rxv4a-manager`

ライセンス: MIT / Copyright (c) 2026 anko

## 1. リポジトリ作成前

- [ ] `git status --short`が空である
- [ ] 公開する全コミットを確認した
- [ ] `.private/`、`private/`、実行時設定、ログが追跡されていない
- [ ] 実IP、MACアドレス、機器ID、トークン、鍵、個人パスがない
- [ ] README、利用説明書、APIガイド、ライセンスを確認した
- [ ] 実機確認済みはRX-V4Aのみと明記されている
- [ ] ほかの機種を動作保証対象として列挙していない
- [ ] Yamaha公式プロジェクトと誤認させる表現やロゴ利用がない

## 2. ローカル検証

```powershell
dotnet restore RxV4A.Manager.sln
dotnet build RxV4A.Manager.sln -c Release --no-restore
dotnet test RxV4A.Manager.sln -c Release --no-build --no-restore
dotnet format RxV4A.Manager.sln --verify-no-changes --no-restore
dotnet publish src/RxV4A.Desktop/RxV4A.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

- [ ] Release buildが警告・エラーなしで成功した
- [ ] 全テストが成功した
- [ ] format検証が成功した
- [ ] self-contained win-x64 publishが成功した
- [ ] 新しいWindowsユーザープロファイル相当で初回起動を確認した
- [ ] 起動時の探索が読み取り専用であることを確認した
- [ ] RX-V4Aで状態取得を確認した
- [ ] 状態変更テストを行う場合、操作ごとに事前確認した

## 3. GitHubリポジトリ設定

推奨説明:

> Windows 11からYamaha Extended Control対応アンプを操作する、Capability駆動の非公式WPFアプリ。実機確認はRX-V4Aのみ。

推奨Topics:

`yamaha`、`musiccast`、`av-receiver`、`windows-11`、`wpf`、`dotnet`、`home-audio`

- [ ] Publicリポジトリとして作成した
- [ ] 既定ブランチを`main`にした
- [ ] GitHub側で別のREADME、LICENSE、`.gitignore`を自動生成していない
- [ ] Actionsの既定権限をRead repository contentsにした
- [ ] `main`のPull RequestとCI成功を要求するルールを検討した
- [ ] Private vulnerability reportingを有効化した
- [ ] IssueとPull Requestテンプレートを確認した
- [ ] Discussions、Wiki、Projectsを必要なものだけ有効化した

## 4. 初回push後

- [ ] GitHub ActionsのCIが成功した
- [ ] README内の相対リンクがGitHub上で開く
- [ ] Security policyがSecurityタブに表示される
- [ ] Issueフォームのセキュリティ報告リンクが正しい
- [ ] GitHubのCode scanning／Dependabot alertsを有効化するか判断した
- [ ] リポジトリURLをプロジェクトメタデータへ追加した

## 5. v0.1.0リリース

- [ ] `CHANGELOG.md`の「未公開」を実際の日付へ変更した
- [ ] `v0.1.0`タグを検証済みコミットへ付けた
- [ ] publish出力をZIP化した
- [ ] ZIP内に実設定、ログ、デバッグシンボル、秘密情報がない
- [ ] Release Notesへ対応範囲、RX-V4Aのみ実機確認、未署名バイナリであることを記載した
- [ ] ZIPをGitHub Releaseへ添付した
- [ ] 公開後にクリーン環境からZIPを再取得して起動確認した

GitHubリポジトリの作成、remote追加、push、タグ作成、Release公開は、それぞれ実行対象を確認してから行います。
