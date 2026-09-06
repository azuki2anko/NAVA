# 変更履歴

このプロジェクトは[Keep a Changelog](https://keepachangelog.com/ja/1.1.0/)の考え方を参考にし、バージョン番号は[Semantic Versioning](https://semver.org/lang/ja/)に従います。

## [Unreleased]

### Fixed

- ミニ電源操作がタスクバー上のアイコンへのクリックを遮らないよう、各モニターの作業領域内へ位置を自動補正

## [0.1.0] - 2026-09-06

### Added

- Yamaha Extended Control対応アンプのCapability駆動検出とMain Zone状態取得
- 電源、入力、音量、ミュート、音場、音声処理、トーン、EQ、バランス操作
- WPFメイン画面、タスクトレイ、最前面ミニ電源トグル
- localhost REST APIとOpenAPI
- グローバルホットキー、汎用アクティビティ、Power-on blocker、登録済みアクション契約
- 構造化ログ、自動再接続、モックテスト
- GitHub ActionsによるWindows Release build、test、format、self-contained publish検証
- 日本語の利用説明書、APIガイド、公開チェックリスト
- Issue／Pull Requestテンプレート、セキュリティポリシー、貢献ガイド
- 操作画面・設定画面のスクリーンショットを使用した利用説明
- 自己完結型Portable ZIPとユーザー別インストーラーの作成スクリプト
- 最小化時のタスクトレイ収納設定とWindowsサインイン時の自動起動設定

### Changed

- 製品の主名称を「NAVA」、副題を「Network AV Amp Controller」へ変更
- 公開製品名を「Network AV Amp Controller」へ変更
- 日常操作画面を、円形ボリュームと電源・ミュート・ソース・音場操作中心の構成へ更新

[Unreleased]: https://github.com/azuki2anko/NAVA/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/azuki2anko/NAVA/releases/tag/v0.1.0
