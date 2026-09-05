# localhost APIガイド

Network AV Amp Controllerは、PC内のほかのアプリから安全に利用するための型付きREST APIを提供します。

## 基本情報

- ベースURL: `http://127.0.0.1:55274/api/v1`
- OpenAPI: `http://127.0.0.1:55274/openapi/v1.json`
- Content-Type: `application/json`
- 既定の待受先: ループバックのみ

このAPIはYamaha APIの汎用プロキシではありません。任意URL、任意コマンド、任意キー列、任意Yamahaエンドポイントは指定できません。

以下の例はPowerShell 7以降の`Invoke-RestMethod`を使用します。

```powershell
$baseUri = 'http://127.0.0.1:55274/api/v1'
Invoke-RestMethod "$baseUri/health"
```

## 読み取りAPI

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/health` | アプリと機器接続の概要 |
| GET | `/device` | 接続状態、モデル、APIバージョン |
| GET | `/device/features` | 公開された対応機能、入力、音場、値域 |
| GET | `/zones/main/status` | Main Zoneの現在状態 |
| GET | `/activities` | 登録済みアクティビティ |
| GET | `/activities/{id}/status` | アクティビティ状態 |
| GET | `/contexts` | Power-on blockerの一覧と状態 |
| GET | `/contexts/{id}` | blockerの状態 |
| GET | `/actions` | 登録済み論理アクション |

操作可能な入力、音場、モード、数値範囲は、先に`/device/features`から取得してください。

```powershell
$features = Invoke-RestMethod "$baseUri/device/features"
$status = Invoke-RestMethod "$baseUri/zones/main/status"
```

## Main Zone操作

すべて明示的な設定操作です。現在値の反転を要求するAPIはありません。

### 電源

```powershell
Invoke-RestMethod "$baseUri/zones/main/power" -Method Put -ContentType 'application/json' -Body '{"power":"on"}'
Invoke-RestMethod "$baseUri/zones/main/power" -Method Put -ContentType 'application/json' -Body '{"power":"standby"}'
```

`force: true`は現在の公開APIでは許可されません。Power-on blockerが有効な場合、ON要求は`409 Conflict`になります。

### 入力

```powershell
$body = @{ input = 'hdmi1' } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/input" -Method Put -ContentType 'application/json' -Body $body
```

`input`は`/device/features`で返されたMain Zone入力IDだけを受け付けます。公開版の対象外である入力は拒否される場合があります。

### 音量とミュート

```powershell
$body = @{ volume = -35.5 } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/volume" -Method Put -ContentType 'application/json' -Body $body

$body = @{ enable = $true } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/mute" -Method Put -ContentType 'application/json' -Body $body
```

音量はMain Zoneの`range_step`にある`volume`の最小値、最大値、刻み幅で検証します。

### 音場プログラム

```powershell
$body = @{ program = 'straight' } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/sound-program" -Method Put -ContentType 'application/json' -Body $body
```

`program`は機器が対応項目として公開した`SoundPrograms`だけを受け付けます。

### 3D Surround、Direct、Pure Direct、Enhancer

```powershell
$enabled = @{ enable = $true } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/processing/3d-surround" -Method Put -ContentType 'application/json' -Body $enabled
Invoke-RestMethod "$baseUri/zones/main/processing/direct" -Method Put -ContentType 'application/json' -Body $enabled
Invoke-RestMethod "$baseUri/zones/main/processing/pure-direct" -Method Put -ContentType 'application/json' -Body $enabled
Invoke-RestMethod "$baseUri/zones/main/processing/enhancer" -Method Put -ContentType 'application/json' -Body $enabled
```

各操作は対応する`surround_3d`、`direct`、`pure_direct`、`enhancer` Capabilityがある場合だけ実行できます。

### トーン

```powershell
$body = @{ mode = 'manual'; bass = 1.0; treble = -0.5 } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/tone" -Method Put -ContentType 'application/json' -Body $body
```

`mode`、`bass`、`treble`は省略可能ですが、少なくとも一つ必要です。値は`tone_control_mode_list`と`tone_control`の`range_step`で検証します。

### EQ

```powershell
$body = @{ mode = 'manual'; low = 1; mid = 0; high = -1 } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/equalizer" -Method Put -ContentType 'application/json' -Body $body
```

値は`equalizer_mode_list`と`equalizer`の`range_step`で検証します。

### バランス

```powershell
$body = @{ value = -2 } | ConvertTo-Json
Invoke-RestMethod "$baseUri/zones/main/balance" -Method Put -ContentType 'application/json' -Body $body
```

負数は左、正数は右です。値は`balance`の`range_step`で検証します。

## アクティビティ、blocker、登録済みアクション

```powershell
Invoke-RestMethod "$baseUri/activities/example:activate" -Method Post
Invoke-RestMethod "$baseUri/activities/example:deactivate" -Method Post
Invoke-RestMethod "$baseUri/contexts/example:activate" -Method Post
Invoke-RestMethod "$baseUri/contexts/example:deactivate" -Method Post
Invoke-RestMethod "$baseUri/actions/example:execute" -Method Post
```

`example`は設定または拡張コードで事前登録されたIDへ置き換えます。未登録IDや外部入力で作った任意コマンドは実行できません。公開版の初期状態では個人用アクティビティ、blocker、アクションは登録されていません。

## 主なエラー

| HTTP | code | 意味 |
|---|---|---|
| 400 | `invalid_value` | 値の形式、範囲、刻み幅が不正 |
| 403 | `force_not_authorized` | 公開APIで許可されない強制操作 |
| 404 | `*_not_found` | 未登録の論理ID |
| 409 | `blocked`相当 | Power-on blockerによる競合 |
| 422 | `capability_not_supported` | 機器が対応機能として公開していない |
| 502 | `device_error` | Yamaha APIが操作を完了できない |
| 503 | `device_unavailable` | 機器へ接続できない |
| 504 | `timeout` | 操作がタイムアウトした |

正確なスキーマと、その時点の実装で公開されるパスはOpenAPI文書を参照してください。

## セキュリティ

- 既定APIをインターネットやLANへ転送しないでください。
- リバースプロキシ、ポートフォワーディング、トンネルで外部公開しないでください。
- 応答やログを共有するときは、接続先や識別情報を除去してください。
- ネットワーク設定、再起動、初期化、ファームウェア操作はこの公開APIから提供しません。
