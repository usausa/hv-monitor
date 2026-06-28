# Hyper-V VM モニター アーキテクチャ設計（分散構成 v2）

> ステータス: **確定（2026-06-28 レビュー反映）** / 旧モノリス構成（`spec-draft.md` 5章）を本書で置き換える
> 確定事項: API=REST/Minimal API、認証=APIキー、複数ホスト表示=1表統合

---

## 1. 目的と方針

画面（UI）と Hyper-V 操作を別プロセスに分離し、Hyper-V 操作を **API サーバ**として各ホスト上で動作させる。
画面アプリは設定ファイルに複数の API サーバ（ホスト）を登録し、**1 つの画面で複数ホストをまとめて管理**する。

| 方針 | 反映 |
|---|---|
| 画面と Hyper-V 操作のプロセス分離 | `Monitor.Web`（画面）と `Monitor.Agent`（API）を分離 |
| Hyper-V 操作を API サーバ化 | `Monitor.Agent` を各ホストで動作させ REST API を公開 |
| 画面に設定ファイルで API サーバ登録 | `Monitor.Web` の `appsettings.json` にホスト一覧 |
| 1 画面で複数ホスト管理 | 全ホストへ並列アクセスし 1 表に統合表示 |

---

## 2. 全体構成

```
┌──────────────────────────────────┐
│  Monitor.Web (Blazor Server)  ── 画面アプリ                │  管理者権限 不要 / net10.0
│   - appsettings.json に複数ホスト(APIサーバ)を登録          │
│   - 全ホストへ並列に問い合わせ、1 表へ統合表示・操作        │
│   - HyperVApiClient (HttpClient + X-Api-Key)               │
└───────┬────────────────┬───────────────┘
        │ HTTPS/REST       │ HTTPS/REST           … 設定ホスト数だけ並列
┌───────┴────────┐ ┌────┴───────────┐
│ Monitor.Agent (API)     │ │ Monitor.Agent (API)     │  各 Hyper-V ホスト上 / 管理者権限 / net10.0-windows
│  - Minimal API          │ │  - Minimal API          │
│  - X-Api-Key 認証        │ │  - X-Api-Key 認証        │
│  - Monitor.Core (CIM)    │ │  - Monitor.Core (CIM)    │
└───────┬────────┘ └────┬───────────┘
     Hyper-V (root\virtualization\v2)   Hyper-V
```

---

## 3. プロジェクト構成

```
Monitor.slnx
├─ src/
│  ├─ Monitor.Contracts/   DTO・API 契約（VmInfo, VmState, HostInfo, ShutdownRequest …）
│  │                       TFM: net10.0（Windows 非依存）  参照: なし
│  ├─ Monitor.Core/        CIM による Hyper-V アクセス（HyperVService）
│  │                       TFM: net10.0-windows           参照: Contracts
│  ├─ Monitor.Agent/       API サーバ（Minimal API）
│  │                       TFM: net10.0-windows           参照: Core, Contracts
│  └─ Monitor.Web/         画面（Blazor Server, MudBlazor）
│                          TFM: net10.0                   参照: Contracts のみ（Core 参照は廃止）
```

ポイント:
- **DTO は `Monitor.Contracts` に集約**し Web/Agent/Core で共有。現在 `Monitor.Core` にある `VmInfo`/`VmState` を移動する。
- **`Monitor.Web` は CIM 参照を廃止**するため `net10.0`（Windows 非依存）にできる。
- API クライアントは `Monitor.Web` 内の `Services/HyperVApiClient` に置く（Web 専用のため独立プロジェクト化しない）。

---

## 4. API 設計（Monitor.Agent）

- ベース: `https://<host>:<port>`（既定ポート例: `5100`）
- 認証: 全エンドポイントで HTTP ヘッダー `X-Api-Key: <key>` を検証（`/api/health` は疎通用に認証任意）
- 形式: JSON。操作は**完了まで待って**結果を返す（CIM ジョブ待ちは Core 実装済み）

| メソッド | パス | 説明 | リクエスト | レスポンス |
|---|---|---|---|---|
| GET | `/api/health` | 疎通確認 | - | 200 OK |
| GET | `/api/host` | ホスト情報 | - | `HostInfo { name, isElevated }` |
| GET | `/api/vms` | VM 一覧 | - | `VmInfo[]` |
| POST | `/api/vms/{id}/start` | 開始 | - | 200 / エラー |
| POST | `/api/vms/{id}/resume` | 再開 | - | 200 / エラー |
| POST | `/api/vms/{id}/pause` | 一時停止 | - | 200 / エラー |
| POST | `/api/vms/{id}/save` | 状態保存 | - | 200 / エラー |
| POST | `/api/vms/{id}/shutdown` | 正常シャットダウン | `{ force: bool }` | 200 / エラー |
| POST | `/api/vms/{id}/turnoff` | 強制電源オフ | - | 200 / エラー |

- エラー応答: `ProblemDetails`（ASP.NET Core 標準）。`HyperVException` は 409/500、VM 未検出は 404、認証失敗は 401。
- 操作は長時間化しうるため、Web 側 `HttpClient.Timeout` を長め（例 120 秒）に設定。

### DTO（Monitor.Contracts）
- `VmInfo(Id, Name, VmState State, int? CpuUsagePercent, long? MemoryUsageMb, TimeSpan? Uptime, string? Version)`
- `enum VmState { … }`（既存）
- `HostInfo(string Name, bool IsElevated)`
- `ShutdownRequest(bool Force)`

---

## 5. 画面アプリ（Monitor.Web）

### 設定ファイル（appsettings.json）
```json
{
  "HyperVHosts": [
    { "Name": "Host A", "BaseUrl": "https://192.168.1.10:5100", "ApiKey": "key-a" },
    { "Name": "Host B", "BaseUrl": "https://192.168.1.11:5100", "ApiKey": "key-b" }
  ]
}
```
`HyperVHostsOptions` として Options パターンで読み込む。

### APIクライアント（HyperVApiClient）
- `IHttpClientFactory` で各ホストの `HttpClient` を生成（`BaseUrl`、`X-Api-Key` 付与）。
- 取得は全ホストへ**並列**（`Task.WhenAll`）。1 ホストが落ちても他は表示し、失敗ホストはエラーとして提示。
- 戻り: `HostVmResult(string HostName, IReadOnlyList<VmInfo> Vms, string? Error)`。

### 画面（VmListPage・1 表統合）
- 列: **ホスト** / 名前 / 状態 / CPU / メモリ / 稼働時間 / バージョン / 操作
- ホストでの絞り込みフィルタ、手動更新。
- 各ホストの `isElevated` を表示（ホストごとに権限状態が異なりうる）。
- 操作は対象 VM のホストの Agent を呼ぶ。確認ダイアログ/進捗/トーストは既存部品を流用。
- 到達不可ホストは画面上部にエラー表示（そのホストの VM は一覧から除外）。

---

## 6. セキュリティ・通信

- **HTTPS は将来対応**（2026-06-28 決定で後回し）。現状は Web↔Agent を **HTTP** で運用し、API キーは平文のため **信頼できるネットワーク内での利用を前提**とする。
- 本番化時に HTTPS + 正規証明書を導入する（Agent は Kestrel で証明書を構成、Web 側は OS の信頼ストアで検証）。
- API キーは設定ファイル管理（将来は環境変数/シークレットストアを推奨）。
- Agent は管理者権限で動作（VM 操作のため）。Web は管理者権限不要。

---

## 7. 既存コードからの移行

| 既存 | 変更 |
|---|---|
| `Monitor.Core` の `VmInfo`/`VmState` | `Monitor.Contracts` へ移動 |
| `Monitor.Core`（HyperVService 等） | 維持（Contracts の型を使用）。Agent が DI |
| `Monitor.Web` の `IHyperVService` 直接利用 | `IHyperVApiClient`（HTTP）へ置換 |
| `Monitor.Web` の Core 参照 | 廃止（Contracts 参照に）。TFM を net10.0 に |
| UI 部品（ConfirmDialog/ProgressOverlay/ToastService） | 流用 |
| `VmListPage` | 複数ホスト統合表示に改修（ホスト列・並列取得・ホスト別操作） |

---

## 8. リスクと対応

| リスク | 対応 |
|---|---|
| ネットワーク経由の VM 操作の安全性 | API キー＋HTTPS。将来 mTLS |
| 一部ホストの到達不可 | 並列取得で部分失敗を許容、ホスト別にエラー表示 |
| 操作の長時間化で HTTP タイムアウト | Web 側 Timeout を延長、Agent は完了待ち |
| 自己署名証明書の取り扱い | 開発時のみ許容、本番は正規証明書 |
| Pause/Save の状態コード | 既存どおり実機 VM で最終確認 |
