# Hyper-V VM モニター 仕様

> ステータス: **確定版**（実装済みの内容を記述） / 最終更新: 2026-07-26
> 本書がプロジェクト唯一の仕様書です。過去の仕様ドラフト・実装プランは役目を終えたため統合・削除しました。

---

## 1. 概要・目的

複数の Hyper-V ホスト上の仮想マシン（VM / ゲスト）を、Web ブラウザから一覧表示・監視し、
開始／停止などの基本操作を行うアプリケーション。

- 対象ユーザー: ホストを管理する運用者・開発者（信頼できるネットワーク内での利用を想定）
- 提供形態: 各ホストで動作する API サーバ（`Monitor.Agent`）と、中央の Web UI（`Monitor.Web`）の 2 プロセス構成

### スコープ

| 区分 | 内容 |
|---|---|
| ✅ 対応済み | 複数ホストの VM 一覧統合表示、状態・CPU・メモリ・稼働時間の表示、開始／再開／一時停止／状態保存／シャットダウン／強制停止、ホストメトリクス（CPU・メモリ・ディスク）、自動更新（定期ポーリング）、手動更新 |
| 🔜 将来候補 | HTTPS / 正規証明書、mTLS、スナップショット（チェックポイント）操作、コンソール接続、ユーザー認証／認可、メトリクスの時系列グラフ |
| ❌ 非対象 | VM の新規作成・削除、仮想ディスク／ネットワークの構成変更 |

---

## 2. 全体構成

画面（UI）と Hyper-V 操作を別プロセスに分離し、Hyper-V 操作を **API サーバ**として各ホスト上で動作させる。
画面アプリは設定ファイルに複数の API サーバ（ホスト）を登録し、**1 つの画面で複数ホストをまとめて管理**する。

```
┌──────────────────────────────────┐
│  Monitor.Web (Blazor Server)  ── 画面アプリ                │  管理者権限 不要 / net10.0
│   - appsettings.json に複数ホスト(APIサーバ)を登録          │
│   - BackgroundService が定期ポーリングし全画面へ配信        │
│   - HyperVApiClient (HttpClient + X-Api-Key)               │
└───────┬────────────────┬───────────────┘
        │ HTTP/REST        │ HTTP/REST            … 設定ホスト数だけ並列
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
│  ├─ Monitor.Contracts/   DTO・API 契約
│  │                       TFM: net10.0（Windows 非依存）  参照: なし
│  ├─ Monitor.Core/        CIM による Hyper-V アクセス（HyperVService）
│  │                       TFM: net10.0-windows           参照: Contracts
│  ├─ Monitor.Agent/       API サーバ（Minimal API）
│  │                       TFM: net10.0-windows           参照: Core, Contracts
│  └─ Monitor.Web/         画面（Blazor Server, MudBlazor）
│                          TFM: net10.0                   参照: Contracts のみ
```

| プロジェクト | 依存パッケージ |
|---|---|
| Monitor.Contracts | （なし） |
| Monitor.Core | Microsoft.Management.Infrastructure |
| Monitor.Agent | ASP.NET Core（Sdk.Web 標準） |
| Monitor.Web | MudBlazor（HttpClient は標準） |

- DTO は `Monitor.Contracts` に集約し Web / Agent / Core で共有する。
- `Monitor.Web` は CIM を参照しないため `net10.0`（Windows 非依存）で動作する。
- API クライアントは Web 専用のため `Monitor.Web/Services/HyperVApiClient` に置く。

---

## 4. 機能仕様

### 4.1 VM 一覧表示

- 設定された全ホストの VM を 1 つの表に統合表示する。
- 表示項目: ホスト / 名前 / 状態 / CPU 使用率(%) / メモリ使用量(MB) / 稼働時間 / バージョン
- 状態は `MudChip` で色分けする（実行中=緑、停止=既定、一時停止=黄、保存=青 など）。
- 複数ホスト登録時はホストによる絞り込みフィルタを表示する。
- 到達不可ホストは画面上部にエラー表示し、そのホストの VM は一覧から除外する（他ホストは表示を継続）。

### 4.2 ホストメトリクス

ホストごとのカードに、ホスト自身の稼働状況を表示する。

- CPU 使用率（ゲージ）
- メモリ 使用量 / 総量（ゲージ + 数値）
- 各固定ディスクの空き / 総量（ドライブごとにゲージ）
- ホストごとの管理者権限（`isElevated`）と到達可否のバッジ

使用率に応じてゲージの色を変える（90% 以上=エラー、75% 以上=警告、それ未満=通常）。
メトリクスを取得できないホストは、メトリクス部分のみ非表示とし VM 一覧の表示は継続する。

### 4.3 VM 操作

各行のアクションボタンから実行する。実行前に確認ダイアログを表示する。

| 操作 | 概要 | 実行可能な状態 |
|---|---|---|
| 開始 (Start) | VM を起動する | 停止 / 保存済み |
| 再開 (Resume) | 一時停止から復帰する | 一時停止 |
| 一時停止 (Pause) | 一時停止する | 実行中 |
| 状態保存 (Save) | メモリ状態を保存して停止する | 実行中 |
| シャットダウン (Shutdown) | ゲスト OS に正常終了を要求する（統合サービス必須） | 実行中 |
| 強制停止 (Turn Off) | 電源を即時オフする（データ損失リスクあり、警告を強調） | 実行中 / 一時停止 |

- 実行可否は `VmActionPolicy` が判定し、実行できない操作のボタンは表示しない。
- 遷移中の状態（起動中・停止中・保存中・一時停止中・再開中）ではすべての操作ボタンを表示しない。

### 4.4 操作のフィードバック

- 操作中は全ボタンを無効化し、`ProgressOverlay` で進行中を表示する。
- 完了／失敗をトースト通知し、失敗時はエラーメッセージを画面にも表示する。
- 操作の成否はアプリログに記録する（監査用）。
- 操作完了後に一覧を再取得する。
- 状態遷移は Agent 側で完了まで待機する（`Msvm_ConcreteJob` 監視、タイムアウト 5 分）。

### 4.5 自動更新

- Web の `VmMonitorBackgroundService`（Singleton / `BackgroundService`）が `PeriodicTimer` で
  設定間隔（既定 10 秒）ごとに全ホストへ並列アクセスし、スナップショットを取得する。
- 起動直後に 1 回即時取得し、以降はティックごとに取得する。
- 取得結果は `VmSnapshotBus`（Singleton）へ Publish し、開いている全画面へ配信する。
  - `Current` に最新スナップショットを保持し、画面は開いた瞬間にこれを読んで即描画する。
  - `Updated` イベントで購読者へ通知する。発火はバックグラウンドスレッドのため、
    画面は `InvokeAsync(StateHasChanged)` で UI スレッドへマーシャリングする。
  - 画面は `Dispose` で購読解除する（Blazor 回路のリーク防止）。
- 取得失敗は握りつぶさずログ出力し、前回値を維持する。
- 手動の「更新」ボタンは同じ取得経路を呼び、結果をバスへ Publish する（全画面に反映される）。

---

## 5. API 仕様（Monitor.Agent）

- ベース: `http://<host>:<port>`（既定ポート `5100`）
- 認証: `/api/health` を除く全エンドポイントで HTTP ヘッダー `X-Api-Key: <key>` を検証する。
- 形式: JSON。操作は**完了まで待って**結果を返す。

| メソッド | パス | 説明 | リクエスト | レスポンス |
|---|---|---|---|---|
| GET | `/api/health` | 疎通確認（認証不要） | - | 200 OK |
| GET | `/api/host` | ホスト情報 | - | `HostInfo` |
| GET | `/api/host/metrics` | ホストメトリクス（個別取得用） | - | `HostMetrics` |
| GET | `/api/snapshot` | ホスト情報＋メトリクス＋VM 一覧を一括取得 | - | `HostSnapshot` |
| GET | `/api/vms` | VM 一覧 | - | `VmInfo[]` |
| POST | `/api/vms/{id}/start` | 開始 | - | 200 / エラー |
| POST | `/api/vms/{id}/resume` | 再開 | - | 200 / エラー |
| POST | `/api/vms/{id}/pause` | 一時停止 | - | 200 / エラー |
| POST | `/api/vms/{id}/save` | 状態保存 | - | 200 / エラー |
| POST | `/api/vms/{id}/shutdown` | 正常シャットダウン | `ShutdownRequest` | 200 / エラー |
| POST | `/api/vms/{id}/turnoff` | 強制電源オフ | - | 200 / エラー |

Web の定常取得は `/api/snapshot` のみを使う（ラウンドトリップ削減と取得時点の一貫性のため）。
`/api/host`・`/api/host/metrics`・`/api/vms` は個別取得・疎通確認用に提供する。

### エラー応答

`ProblemDetails`（ASP.NET Core 標準）で返す。

| 状況 | ステータス |
|---|---|
| API キー不正・未指定 | 401 |
| VM 未検出（`VmNotFoundException`） | 404 |
| 操作失敗（`HyperVException`） | 409 |
| 一覧・メトリクス取得失敗 | 500 |

`/api/snapshot` はメトリクス取得のみが失敗した場合、500 にせず `metrics: null` を返して
VM 一覧の取得結果を維持する（部分表示を許容する）。

### DTO（Monitor.Contracts）

```csharp
VmInfo(string Id, string Name, VmState State, int? CpuUsagePercent, long? MemoryUsageMb, TimeSpan? Uptime, string? Version)
HostInfo(string Name, bool IsElevated)
HostMetrics(double CpuUsagePercent, long TotalMemoryMb, long UsedMemoryMb, IReadOnlyList<DiskInfo> Disks)
DiskInfo(string Name, long TotalBytes, long FreeBytes)
HostSnapshot(HostInfo Host, HostMetrics? Metrics, IReadOnlyList<VmInfo> Vms)
ShutdownRequest(bool Force)

enum VmState { Unknown, Running, Off, Stopping, Saved, Paused, Starting, Saving, Pausing, Resuming, Other }
```

単位の整形（GB 表記など）は Web 側で行う。

---

## 6. Hyper-V アクセス（Monitor.Core）

CIM（`Microsoft.Management.Infrastructure`）に統一する。取得・操作とも同一の `CimSession` 基盤を使う。

### ゲスト情報（`root\virtualization\v2`）

| 用途 | クラス / メソッド |
|---|---|
| VM 一覧・メトリクス | `Msvm_SummaryInformation`（`ElementName` / `EnabledState` / `ProcessorLoad` / `MemoryUsage` / `UpTime` / `Version`） |
| 状態変更 | `Msvm_ComputerSystem.RequestStateChange` |
| 正常シャットダウン | `Msvm_ShutdownComponent.InitiateShutdown`（`Msvm_SystemDevice` 経由で取得） |
| 非同期ジョブの完了待ち | `Msvm_ConcreteJob`（200ms 間隔でポーリング、タイムアウト 5 分） |

`EnabledState` → `VmState` の対応は `VmStateMapper` に定義する
（2=Running、3=Off、32768=Paused、32769=Saved、32770=Starting、32773=Saving、32774=Stopping、32776=Pausing、32777=Resuming）。
CPU / メモリ / 稼働時間は実行中（`EnabledState == 2`）の VM のみ値を持ち、それ以外は null とする。

`RequestStateChange` の戻り値は 0（完了）と 4096（ジョブ開始）を成功として扱い、
4096 の場合は `Msvm_ConcreteJob` の `JobState` が 7（完了）になるまで待つ。7 を超える値は失敗として扱う。

### ホストメトリクス（`root\cimv2`）

| 指標 | クラス | プロパティ | 備考 |
|---|---|---|---|
| CPU 使用率 | `Win32_PerfFormattedData_Counters_HyperVHypervisorLogicalProcessor`（`Name='_Total'`） | `PercentTotalRunTime` | 第 1 候補。Hyper-V ホストではルートパーティションの値より実負荷を正確に反映する |
| CPU 使用率（代替） | `Win32_PerfFormattedData_PerfOS_Processor`（`Name='_Total'`） | `PercentProcessorTime` | 第 2 候補 |
| CPU 使用率（最終手段） | `Win32_Processor` | `LoadPercentage` | 第 3 候補。粗いが可用性が高い |
| メモリ総量 / 空き | `Win32_OperatingSystem` | `TotalVisibleMemorySize` / `FreePhysicalMemory` | KB 単位。使用量 = 総量 − 空き |
| ディスク空き | `Win32_LogicalDisk`（`DriveType=3`） | `DeviceID` / `Size` / `FreeSpace` | バイト単位。固定ローカルディスクのみ |

パフォーマンスカウンタ クラスは環境により無効化されている場合があるため、CPU は上記 3 段のフォールバックで取得する。

---

## 7. 設定

### Monitor.Agent（appsettings.json）

```json
{
  "Agent": { "ApiKey": "REPLACE_WITH_A_STRONG_RANDOM_KEY" },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5100" } } }
}
```

### Monitor.Web（appsettings.json）

```json
{
  "Monitoring": {
    "Enabled": true,
    "RefreshIntervalSeconds": 10
  },
  "HyperVHosts": [
    { "Name": "Host A", "BaseUrl": "http://192.168.1.10:5100", "ApiKey": "key-a" },
    { "Name": "Host B", "BaseUrl": "http://192.168.1.11:5100", "ApiKey": "key-b" }
  ]
}
```

- `HyperVHostsOptions` / `MonitoringOptions` として Options パターンで読み込む。
- `Monitoring:Enabled` を false にすると自動更新を停止し、手動更新のみになる。
- 操作は長時間化しうるため、Web 側 `HttpClient.Timeout` は 120 秒とする。

---

## 8. 非機能要件

| 区分 | 要件 |
|---|---|
| 動作環境 | Agent: Windows 10/11 または Windows Server（Hyper-V 役割が有効）。Web: Windows 非依存 |
| ランタイム | .NET 10 / C#（Directory.Build.props に準拠） |
| 権限 | Agent は管理者権限で実行（VM 操作のため）。Web は管理者権限不要 |
| 性能 | VM 数十台規模で一覧取得が体感数秒以内。ポーリングは Web 集約のためクライアント数に依存しない |
| 品質 | ビルド警告ゼロ（StyleCop / CA / Nullable をエラー扱い）。コードコメントは英語 |
| 可用性 | 1 ホストの障害が全体を落とさない（部分失敗を許容） |

---

## 9. セキュリティ・通信

- 認証は **API キー**（`X-Api-Key`）。キーはホストごとに別の強いランダム値を設定する。
- **HTTPS は未対応**。現状は Web ↔ Agent を HTTP で運用し、API キーは平文で流れるため
  **信頼できるネットワーク内での利用を前提**とする。本番化時に HTTPS + 正規証明書を導入する。
- API キーは設定ファイルで管理する（将来は環境変数／シークレットストアを推奨）。
- VM の操作履歴はアプリログに記録する。

---

## 10. 設計判断の記録

| ID | 論点 | 決定 |
|---|---|---|
| A | Hyper-V アクセス方式 | **CIM に統一**（`Microsoft.Management.Infrastructure`）。取得・操作とも CIM |
| B | プロセス構成 | **画面と Hyper-V 操作を分離**。Agent を各ホストに配置し REST API 化 |
| C | 「停止」操作 | **両方提供**（正常シャットダウン＋強制電源オフ）。強制側は警告を強化した確認ダイアログ |
| D | 更新方式 | **Web 集約の定期ポーリング**＋手動更新。各ブラウザが個別に Agent を叩かない |
| E | 管理者権限 | **起動時に検知して UI 表示**（manifest による強制はしない） |
| F | 複数ホスト表示 | **1 表に統合**（ホスト列＋ホストフィルタ） |
| G | API 形式・認証 | **REST / Minimal API**、**API キー**認証 |
| H | HTTPS | **後回し**。HTTP 運用とし、信頼ネットワーク前提 |
| I | メトリクス取得 | `/api/snapshot` に統合し 1 リクエストで VM 一覧とホストメトリクスを取得 |

---

## 11. 既知の制約・リスク

| 項目 | 内容・対応 |
|---|---|
| 通信の平文化 | HTTPS 未対応のため信頼ネットワーク内に限定する。将来 HTTPS / mTLS を導入 |
| 一部ホストの到達不可 | 並列取得で部分失敗を許容し、ホスト別にエラー表示する |
| CPU カウンタの可用性・精度 | 3 段フォールバックで取得。全滅時は 0% として表示する |
| 管理者権限不足 | ホストカードに「非管理者」バッジを表示。操作失敗はトーストで明示する |
| 長時間ジョブ | Agent 側で完了まで待機（5 分でタイムアウト）。Web 側 Timeout は 120 秒 |
| Blazor Server の接続断 | 再接続後、`VmSnapshotBus.Current` から最新値を即時復元する |
| Pause / Save の状態コード | 実機 VM での最終確認を推奨（開発機に検証用 VM が無いため未検証） |
