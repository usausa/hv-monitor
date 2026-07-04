# リアルタイム更新 & ホストメトリクス 拡張プラン

> ステータス: 設計（ドキュメント先行・実装前） / 2026-06-29
> 対象: docs/architecture.md（分散構成 v2、V0–V5 完了）への追加機能の設計検討
> 注: リアルタイム更新は Web 側の BackgroundService（PeriodicTimer）+ イベントバスで実現する。

---

## 0. 背景と目的

現状（V0–V5 完了時点）:
- Monitor.Web は Blazor Server。VM 一覧は手動の「更新」ボタンによるプル取得のみ。
- メトリクスは ゲスト VM のみ（Msvm_SummaryInformation の ProcessorLoad / MemoryUsage / UpTime）。

本書で検討する 2 つの拡張:
1. リアルタイム更新: 画面を開いている間、Web が各 Agent と定期通信して常に最新状態を表示し続ける。
2. ホストメトリクス: ゲストに加え、ホスト自身の CPU 使用率・メモリ使用量・各ディスクの空き容量を取得・表示する。

---

# Part 1: リアルタイム更新（BackgroundService + イベントバス）

## 1.1 方針

- Web 側で集約してポーリングする。各ブラウザが個別に Agent を叩くのではなく、Web の単一バックグラウンド処理がまとめて取得する（Agent への負荷がクライアント数に依存しない）。
- 取得した最新データは イベントバス（Singleton） に置き、開いている各画面へ配信する。
- Blazor Server のため、サーバー側でコンポーネントの状態を変えて StateHasChanged を呼べば、既存の常時接続を通じてブラウザに反映される（新たな通信機構は不要）。

## 1.2 構成要素

### (a) 取得ループ: VmMonitorBackgroundService（Singleton / BackgroundService）
- PeriodicTimer で 設定値の間隔（既定 10 秒）ごとに実行。
- 起動直後に 1 回即時取得し、以降はティックごとに取得（初回をティック待ちしない）。
- 各回で全 Agent から最新情報を並列取得（IHyperVApiClient。後述の /api/snapshot で VM 一覧＋ホストメトリクスを一括取得）。
- 取得できたらイベントバスへ Publish。失敗は握りつぶさずログ出力し、前回値を維持。

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
    do
    {
        try
        {
            var snapshot = await client.GetSnapshotAsync(stoppingToken);
            bus.Publish(snapshot);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Snapshot polling failed");
        }
    }
    while (await timer.WaitForNextTickAsync(stoppingToken));
}
```

`do { 取得 } while (await timer.WaitForNextTickAsync(...))` とすることで「起動時に即取得 → 以降は間隔ごと」を満たす。

### (b) イベントバス: IVmSnapshotBus / VmSnapshotBus（Singleton / .NET 標準 event）
- 最新スナップショットを常に保持（Current）。これにより、画面はイベントを待たずに その時点の最新値を即取得 できる。
- 更新時に event（.NET 標準）を発火して購読者へ通知。

```csharp
public sealed class VmSnapshotEventArgs(IReadOnlyList<HostVmResult> snapshot) : EventArgs
{
    public IReadOnlyList<HostVmResult> Snapshot { get; } = snapshot;
}

public interface IVmSnapshotBus
{
    // 直近に取得済みのスナップショット（初期値は空）。画面起動時の即時表示に使う。
    IReadOnlyList<HostVmResult> Current { get; }

    // 最新データ更新の通知（.NET 標準 event）。
    event EventHandler<VmSnapshotEventArgs>? Updated;

    // BackgroundService から最新データを流し込む。
    void Publish(IReadOnlyList<HostVmResult> snapshot);
}
```

Publish は Current を差し替え、Updated を発火する（実装は素朴な event で良い）。

### (c) 画面（VmListPage 等）: 購読者
- 開いたとき（初期化時）: まず Bus.Current を読み込んで即描画 → その後 Updated を購読。イベントを待たずに表示できる。
- 更新通知時: InvokeAsync(StateHasChanged) で再描画（バックグラウンド スレッドから Circuit の同期コンテキストへマーシャリング）。
- 閉じるとき（破棄時）: Updated を購読解除（IDisposable）。回路リーク防止に必須。

```csharp
protected override void OnInitialized()
{
    hostResults = Bus.Current;        // その時点の最新を即表示（待たない）
    Bus.Updated += OnSnapshotUpdated;
}

private void OnSnapshotUpdated(object? sender, VmSnapshotEventArgs e) =>
    InvokeAsync(() =>
    {
        hostResults = e.Snapshot;
        StateHasChanged();
    });

public void Dispose() => Bus.Updated -= OnSnapshotUpdated;
```

## 1.3 データフロー

```
VmMonitorBackgroundService (Singleton)
  └─ PeriodicTimer(10s, 設定値) ／ 起動時は即時1回
       └─ IHyperVApiClient.GetSnapshotAsync() → 各 Agent へ並列（/api/snapshot）
            └─ VmSnapshotBus.Publish(snapshot)
                 ├─ Current を最新へ差し替え（次に開く画面の初期表示用）
                 └─ Updated 発火
                      ├─→ 画面#1: InvokeAsync(StateHasChanged) → ブラウザA
                      └─→ 画面#2: InvokeAsync(StateHasChanged) → ブラウザB
（ブラウザ↔Web は Blazor Server の既存接続がそのまま差分を配信）
```

## 1.4 設定（appsettings.json）

```jsonc
"Monitoring": {
  "Enabled": true,
  "RefreshIntervalSeconds": 10
}
```

## 1.5 設計上の考慮事項

- DI: VmMonitorBackgroundService と VmSnapshotBus、および取得クライアント（IHyperVApiClient）はいずれも Singleton とし、単一のポーリング処理と全画面で共有する。取得クライアントは状態を持たずスレッドセーフに保つ。VmMonitorBackgroundService は HostedService として登録する。
- スレッド安全:
  - Current は不変リストの差し替えで共有し、読み手（各画面）は参照を読むだけにする。
  - event はバックグラウンド スレッドで発火するため、各画面は InvokeAsync(StateHasChanged) で UI スレッドへマーシャリングする。
  - 購読解除の直後にハンドラが呼ばれるレースに備え、ハンドラは破棄後の操作に耐えるようにする。
- 初回表示: Current の初期値は空。画面は常に Current を即読みし、空の間はローディング表示とする。起動直後の即時取得で速やかに埋まる。
- 常時取得 vs 休止（任意）: 基本は「常に最新を取得」とし、Web 起動中は購読者の有無にかかわらずポーリングを継続する。負荷が問題化したら「購読者 0 のとき休止」を任意で追加できる。
- 操作後の即時反映（任意）: VM 操作の完了後、次ティックを待たずに即時取得を要求できる仕組み（バス or サービスの「今すぐ更新」）を設ける。

---

# Part 2: ホストメトリクス取得

## 2.1 取得対象と CIM ソース

ゲストメトリクスは root\virtualization\v2（Msvm_*）だが、ホスト自身の指標は root\cimv2（Win32_*）から取得する。同一 CimSession で名前空間を変えてクエリ可能。

| 指標 | クラス（root\cimv2） | 主プロパティ | 単位 / 備考 |
|---|---|---|---|
| CPU 使用率 | Win32_PerfFormattedData_PerfOS_Processor（Name='_Total'） | PercentProcessorTime | %（瞬間値。カウンタは事前計算済み） |
| CPU 使用率（Hyper-V 特化・代替） | Win32_PerfFormattedData_Counters_HyperVHypervisorLogicalProcessor（Name='_Total'） | PercentTotalRunTime | %。論理プロセッサ全体（ゲスト+ハイパーバイザ）の実負荷。Hyper-V ホストではルートパーティションの PerfOS_Processor が実負荷を過小に見せる場合があり、こちらがより正確 |
| メモリ総量 / 空き | Win32_OperatingSystem | TotalVisibleMemorySize / FreePhysicalMemory | KB。使用量 = 総量 − 空き |
| ディスク空き | Win32_LogicalDisk（DriveType=3） | DeviceID / Size / FreeSpace | バイト。DriveType=3＝固定ローカルディスクに限定 |

補足:
- CPU カウンタの最終選定は実機検証が必要（要検証）。Hyper-V ホストでは HyperVHypervisorLogicalProcessor を第一候補、PerfOS_Processor をフォールバックに。
- パフォーマンスカウンタ クラスが一部環境で無効化されている可能性に備え、Win32_Processor.LoadPercentage（粗いが常用可）をさらなるフォールバックに。

## 2.2 DTO（Monitor.Contracts への追加案）

静的情報（HostInfo）と変動情報（メトリクス）は分離する。

```csharp
public sealed record HostMetrics(
    double CpuUsagePercent,
    long TotalMemoryMb,
    long UsedMemoryMb,
    IReadOnlyList<DiskInfo> Disks);

public sealed record DiskInfo(
    string Name,        // 例 "C:"
    long TotalBytes,
    long FreeBytes);
```

表示の単位整形（GB 等）は Web 側で実施（既存 FormatMemory と同方針）。

## 2.3 各層の変更

### Core（Monitor.Core）
- IHyperVService に ValueTask<HostMetrics> GetHostMetricsAsync(CancellationToken) を追加。
- HyperVService に root\cimv2 への WQL クエリを実装（既存の CimSession／ヘルパー（GetValue/GetString）を流用）。
- CimConstants に定数追加（既存スタイル踏襲）:

```csharp
public const string Cimv2Namespace = @"root\cimv2";
public const string OperatingSystemClass = "Win32_OperatingSystem";
public const string LogicalDiskClass = "Win32_LogicalDisk";
public const string ProcessorPerfClass = "Win32_PerfFormattedData_PerfOS_Processor";
// 代替: "Win32_PerfFormattedData_Counters_HyperVHypervisorLogicalProcessor"
```

### Agent（Monitor.Agent）
- スナップショット統合（推奨）: GET /api/snapshot で { host: HostInfo, metrics: HostMetrics, vms: VmInfo[] } を 1 回で返す。
  - 利点: ラウンドトリップ削減・取得時点の一貫性・リアルタイム取得ループと好相性（1 回の取得で全部更新できる）。
  - 既存の /api/host /api/vms、および必要なら /api/host/metrics は個別取得用に残してよい。

### Web（Monitor.Web）
- HostVmResult を拡張: HostVmResult(string HostName, bool IsElevated, HostMetrics? Metrics, IReadOnlyList<VmInfo> Vms, string? Error)。
- IHyperVApiClient に GetSnapshotAsync（/api/snapshot）を追加。BackgroundService はこれを使う。
- ホスト状態カードに以下を表示:
  - CPU 使用率（MudProgressLinear 等のゲージ）
  - メモリ 使用量 / 総量（ゲージ + 数値）
  - 各ディスクの空き（ドライブごとにバー + 空き/総量）
- 到達不可・非対応時は Metrics=null として欠落表示（部分表示を許容）。

## 2.4 リアルタイムとの統合
- Part 1 の VmMonitorBackgroundService が /api/snapshot を 1 回叩くだけで VM 一覧とホストメトリクスを同時更新し、イベントバス経由で各画面へ配信する。両機能を別々に取得しない。

---

## 3. 実装マイルストーン（案）

各段で ビルド警告ゼロ を完了条件に含める（AGENTS.md 方針）。

| # | 内容 | DoD |
|---|---|---|
| R1 | ホストメトリクス取得（Core CIM + DTO + Agent /api/host/metrics） | このホストの Agent で metrics が実値を返す（CPU/メモリ/ディスク）。警告ゼロ |
| R2 | スナップショット統合 API /api/snapshot（host+metrics+vms）と HostVmResult 拡張・GetSnapshotAsync | 1 リクエストで 3 種を取得。警告ゼロ |
| R3 | Web 表示（ホストカードに CPU/メモリ/ディスク ゲージ） | 手動更新でホストメトリクスが表示される。警告ゼロ |
| R4 | リアルタイム更新（VmSnapshotBus + VmMonitorBackgroundService + 画面の購読/解除 + 設定） | 画面を開くと自動更新、起動時は Current で即時表示、間隔設定が効く、購読解除でリーク無し、複数タブで共有 |

順序の意図: 先に取得（R1/R2）と表示（R3）を確定し、最後に更新機構（R4）を被せる。R4 は取得経路（GetSnapshotAsync）を流用し、画面は Current 即読み + イベント購読に差し替えるだけで済む。

## 4. リスクと考慮事項

| リスク | 対応 |
|---|---|
| ポーリング負荷（間隔 × ホスト数） | Web 集約で一定化。間隔を設定可能に。必要なら購読 0 で休止（任意） |
| CPU カウンタの精度（ホスト/Hyper-V） | HyperVHypervisorLogicalProcessor 優先、PerfOS_Processor／Win32_Processor をフォールバック。実機検証必須 |
| パフォーマンスカウンタ クラスの可用性差 | 取得失敗時はそのメトリクスのみ欠落表示（全体は継続） |
| 非管理者・到達不可ホスト | Metrics=null で部分表示。既存のホスト別エラー表示を流用 |
| Blazor 回路リーク | コンポーネントの Dispose で確実に購読解除 |
| Singleton から Scoped 依存の解決 | HyperVApiClient を Singleton 化（ステートレス）or スコープ生成 |
| event 発火とスレッド | 発火はバックグラウンド → 画面は InvokeAsync(StateHasChanged)。Current は不変差し替え |
| 更新間隔と取得時間の整合 | ポーリング用に短めの実効タイムアウトを別途検討（間隔 > 1 回の取得時間） |

## 5. 既存設計との関係
- 本拡張は architecture.md（分散構成 v2）/ implementation-plan.md（V0–V5）への追加。
- HTTPS/証明書は引き続き後回し（HTTP 運用）。本拡張は HTTP 前提で成立する。
- 時系列グラフ（スパークライン等）は本書の対象外（瞬間値のみ）。将来の別検討とする。
