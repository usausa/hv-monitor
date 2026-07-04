# hv-monitor

複数の Hyper-V ホストをまたいで仮想マシン（VM）を監視・操作する分散型アプリケーションです。
各ホスト上で動作する API サーバ（**Monitor.Agent**）と、それらを 1 画面に統合表示する Web UI（**Monitor.Web**）の 2 プロセスで構成します。

## アーキテクチャ

```
                    ┌─────────────────────────┐
   ブラウザ ──────▶ │ Monitor.Web (Blazor Server) │  中央 UI / 管理者権限 不要 / net10.0
                    │  appsettings.json に複数ホストを登録   │
                    └───────┬─────────────┬───────┘
                  HTTP/REST │             │ HTTP/REST   … 設定ホスト数だけ並列
                  X-Api-Key │             │ X-Api-Key
              ┌─────────────┴───┐   ┌─────┴───────────┐
              │ Monitor.Agent (API) │   │ Monitor.Agent (API) │  各 Hyper-V ホスト / 管理者権限 / net10.0-windows
              │  Minimal API         │   │  Minimal API         │
              └─────────┬───────┘   └─────────┬───────┘
                   Hyper-V (CIM)          Hyper-V (CIM)
```

Web は全ホストへ並列に問い合わせ、結果を 1 つの表に統合表示します。1 ホストが到達不可でも他ホストは表示し、失敗ホストは画面上にエラーとして提示します。

## 機能

- 複数ホストの VM 一覧を 1 表に統合表示（ホスト・名前・状態・CPU・メモリ・稼働時間・バージョン）
- ホストでの絞り込み、手動更新、ホストごとの管理者権限（isElevated）/到達可否の表示
- VM 操作: 開始 / 再開 / 一時停止 / 状態保存 / シャットダウン（正常終了）/ 強制停止（電源オフ）
- 状態に応じた操作ボタンの出し分け、操作前の確認ダイアログ、進捗・成否のトースト通知、操作の監査ログ出力

## プロジェクト構成

| プロジェクト | TFM | 役割 | 参照 |
|---|---|---|---|
| `src/Monitor.Contracts` | net10.0 | DTO・API 契約（`VmInfo`/`VmState`/`HostInfo`/`ShutdownRequest`） | なし |
| `src/Monitor.Core` | net10.0-windows | Hyper-V アクセス（CIM / Microsoft.Management.Infrastructure） | Contracts |
| `src/Monitor.Agent` | net10.0-windows | API サーバ（Minimal API、X-Api-Key 認証） | Core, Contracts |
| `src/Monitor.Web` | net10.0 | 中央 UI（Blazor Server、MudBlazor、HTTP クライアント） | Contracts |

## 必要要件

- **Monitor.Agent を動かすホスト**: Windows 10/11 または Windows Server（Hyper-V の役割が有効）/ .NET 10 SDK / VM 操作のため **管理者権限** で実行
- **Monitor.Web を動かすホスト**: .NET 10 SDK（Windows 非依存）/ 管理者権限は不要

## セットアップ

### 1. Monitor.Agent（各 Hyper-V ホストに配置）

1. `src/Monitor.Agent/appsettings.json` の `Agent:ApiKey` を、推測されない強いランダム値に変更します（ホストごとに別の値を推奨）。
2. 待ち受けポート（既定 `5100`）を必要に応じて変更します（`Kestrel:Endpoints` または起動時の `ASPNETCORE_URLS`）。
3. ファイアウォールで該当ポートを開放します。
4. **管理者として** 起動します。

```
dotnet run --project src/Monitor.Agent
```

疎通確認:

```
curl http://localhost:5100/api/health                       # 200 OK（認証不要）
curl -H "X-Api-Key: <KEY>" http://localhost:5100/api/vms     # VM 一覧（JSON）
```

### 2. Monitor.Web（中央に 1 つ配置）

1. `src/Monitor.Web/appsettings.json` の `HyperVHosts` に、各 Agent の `Name` / `BaseUrl` / `ApiKey` を登録します。
2. 起動します（管理者権限は不要）。

```
dotnet run --project src/Monitor.Web
```

起動後、表示される URL（開発既定: `http://localhost:5026`）にブラウザでアクセスします。

## 開発時クイックスタート（同一マシンで 2 プロセス）

```
# ターミナル A: Agent（ポート 5100）
dotnet run --project src/Monitor.Agent --launch-profile http

# ターミナル B: Web（ポート 5026）
dotnet run --project src/Monitor.Web --launch-profile http
```

`Monitor.Web` の既定 `appsettings.json` には、ローカル Agent（`Local` / `http://localhost:5100` / `change-me`）が登録済みです。
設定例は [`src/Monitor.Agent/appsettings.Sample.json`](src/Monitor.Agent/appsettings.Sample.json) /
[`src/Monitor.Web/appsettings.Sample.json`](src/Monitor.Web/appsettings.Sample.json) を参照してください。

## API（Monitor.Agent）

`/api/health` を除く全エンドポイントで HTTP ヘッダー `X-Api-Key: <key>` を検証します。

| メソッド | パス | 説明 | リクエスト |
|---|---|---|---|
| GET | `/api/health` | 疎通確認（認証不要） | - |
| GET | `/api/host` | ホスト情報 `{ name, isElevated }` | - |
| GET | `/api/vms` | VM 一覧 | - |
| POST | `/api/vms/{id}/start` | 開始 | - |
| POST | `/api/vms/{id}/resume` | 再開 | - |
| POST | `/api/vms/{id}/pause` | 一時停止 | - |
| POST | `/api/vms/{id}/save` | 状態保存 | - |
| POST | `/api/vms/{id}/shutdown` | 正常シャットダウン | `{ "force": bool }` |
| POST | `/api/vms/{id}/turnoff` | 強制電源オフ | - |

エラーは `ProblemDetails` で返します（VM 未検出は 404、操作失敗は 409/500、認証失敗は 401）。

## セキュリティ

- 認証は **API キー**（`X-Api-Key`）。キーは設定ファイルで管理します（将来は環境変数／シークレットストアを推奨）。
- 通信は現状 **HTTP**（開発）。API キーは平文のため、**HTTPS 化までは信頼できるネットワーク内での利用**を前提とします。
  本番は HTTPS + 正規証明書での運用を推奨します（今後対応）。
- `Monitor.Agent` は管理者権限で動作（VM 操作のため）、`Monitor.Web` は管理者権限不要です。

## ビルド

```
dotnet build Monitor.slnx
```

## 技術

- Blazor Server（.NET 10）/ MudBlazor
- Hyper-V アクセス: CIM（名前空間 `root\virtualization\v2`、`Msvm_*` クラス）
  - 一覧取得: `Msvm_SummaryInformation`
  - 状態変更: `Msvm_ComputerSystem.RequestStateChange`（非同期ジョブは `Msvm_ConcreteJob` を監視）
  - 正常シャットダウン: `Msvm_ShutdownComponent.InitiateShutdown`

## ドキュメント

- アーキテクチャ: [docs/architecture.md](docs/architecture.md)
- 実装プラン: [docs/implementation-plan.md](docs/implementation-plan.md)
- 拡張設計（リアルタイム更新 & ホストメトリクス）: [docs/realtime-and-metrics-plan.md](docs/realtime-and-metrics-plan.md)
- 仕様（旧モノリス版の草案）: [docs/spec-draft.md](docs/spec-draft.md)

## 注意

- 「強制停止」はゲスト OS の電源を即座に切るため、未保存データが失われる可能性があります。
- 「一時停止」「状態保存」など一部操作の状態コードは、実機 VM での最終確認を推奨します。
