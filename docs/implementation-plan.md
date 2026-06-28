# Hyper-V VM モニター 実装プラン

> 対象: `docs/spec-draft.md`（決定事項 A〜F 反映済み） / 作成: 2026-06-27
> 方針: **CIM 統一 / `Monitor.Web` + `Monitor.Core` の 2 分割 / 停止は両系統 / 起動時に権限検知**

---

## 1. 確定した前提（サマリ）

| 項目 | 決定 |
|---|---|
| アクセス方式 | CIM（`Microsoft.Management.Infrastructure`）で取得・操作 |
| 構成 | `src/Monitor.Web`（Blazor Server, MudBlazor）+ `src/Monitor.Core`（Hyper-V アクセス） |
| 停止操作 | 正常シャットダウン ＋ 強制電源オフ の両方 |
| 権限 | 管理者強制はせず、起動時に検知して UI 警告 |
| 自動更新 | 初版は手動更新ボタンのみ |
| TFM | `net10.0-windows`（CIM/Hyper-V は Windows 専用） |

---

## 2. 最終的なプロジェクト構成

```
Monitor.slnx                         (既存。プロジェクト参照を追加)
├─ src/
│  ├─ Monitor.Core/                  net10.0-windows / クラスライブラリ
│  │  ├─ Monitor.Core.csproj
│  │  ├─ Models/
│  │  │  ├─ VmInfo.cs                (record: 一覧表示用 DTO)
│  │  │  ├─ VmState.cs               (enum: 状態)
│  │  │  └─ VmAction.cs             (enum: 操作種別。UI 出し分けに利用)
│  │  ├─ HyperV/
│  │  │  ├─ IHyperVService.cs        (取得/操作のインターフェース)
│  │  │  ├─ HyperVService.cs         (CimSession 実装)
│  │  │  ├─ CimConstants.cs          (名前空間/クラス/状態値の定数)
│  │  │  └─ VmStateMapper.cs         (EnabledState <-> VmState 変換)
│  │  └─ Security/
│  │     └─ ElevationChecker.cs      (管理者権限の判定)
│  └─ Monitor.Web/                   net10.0-windows / Blazor Server
│     ├─ Monitor.Web.csproj
│     ├─ Program.cs
│     ├─ appsettings.json
│     ├─ GlobalUsing.cs
│     ├─ Components/
│     │  ├─ App.razor / Routes.razor / _Imports.razor
│     │  ├─ Layout/MainLayout.razor
│     │  ├─ Pages/VmListPage.razor (+ .razor.cs)   @page "/"
│     │  └─ Shared/
│     │     ├─ ConfirmDialog.razor
│     │     └─ ProgressOverlay.razor
│     └─ Services/
│        ├─ ToastService.cs          (MudSnackbar ラッパー)
│        └─ VmActionPolicy.cs        (状態ごとの可否・色・ラベル)
└─ docs/
   ├─ spec-draft.md
   └─ implementation-plan.md
```

> `Monitor.slnx` には `src/Monitor.Core/Monitor.Core.csproj` と
> `src/Monitor.Web/Monitor.Web.csproj` を追加する。`Monitor.Web` は `Monitor.Core` を参照。

---

## 3. 依存パッケージ

| プロジェクト | パッケージ | 用途 |
|---|---|---|
| Monitor.Core | `Microsoft.Management.Infrastructure` | CIM セッション・クエリ・メソッド呼び出し |
| Monitor.Web | `MudBlazor`（9.x） | UI コンポーネント |

- ルートの `Directory.Build.props` で StyleCop / JapaneseComment / Nullable-as-error が
  全プロジェクトに適用される。**コードコメントは英語**で記述する。
- 既存方針（メンバ変数に `_` 接頭辞なし、警告ゼロ）を厳守する。

---

## 4. CIM による Hyper-V アクセス設計

### 4.1 名前空間とクラス（`CimConstants`）
- 名前空間: `root\virtualization\v2`
- 一覧取得: `Msvm_SummaryInformation`（参考実装と同一。VM 単位の状態/CPU/メモリ/Uptime/Version を一括取得）
- 操作対象: `Msvm_ComputerSystem`（`Name`=GUID で特定）
- 正常シャットダウン: `Msvm_ShutdownComponent`（`Msvm_ComputerSystem` から関連で取得）
- 非同期完了監視: `Msvm_ConcreteJob`

### 4.2 一覧取得
```csharp
using var session = CimSession.Create(null); // null = localhost
var rows = session.QueryInstances(
    CimConstants.Namespace, "WQL",
    "SELECT Name, ElementName, EnabledState, OnTimeInMilliseconds, " +
    "ProcessorLoad, MemoryUsage, Version FROM Msvm_SummaryInformation");
// map each CimInstance -> VmInfo (state==2 のときのみ CPU/メモリ/Uptime を採用)
```
- 参考実装（`HyperVInstrumentation.cs`）の抽出ロジックをそのまま移植する。
- `EnabledState==2`（実行中）以外は CPU/メモリ/Uptime を「-」表示にする。

### 4.3 操作（状態変更）
`Msvm_ComputerSystem` を GUID で特定し `RequestStateChange(RequestedState)` を呼ぶ。
```csharp
var vm = session.QueryInstances(ns, "WQL",
    $"SELECT * FROM Msvm_ComputerSystem WHERE Name='{guid}'").First();
var p = new CimMethodParametersCollection {
    CimMethodParameter.Create("RequestedState", requestedState, CimType.UInt16, CimFlags.In) };
var r = session.InvokeMethod(ns, vm, "RequestStateChange", p);
// ReturnValue: 0=完了, 4096=ジョブ開始(out "Job" を監視), それ以外=失敗
```

| 操作 | 実装 | RequestedState（実装時に Microsoft Learn で最終確定） |
|---|---|---|
| 開始 Start | RequestStateChange | `2` (Enabled) |
| 強制停止 Turn Off | RequestStateChange | `3` (Disabled) |
| 一時停止 Pause | RequestStateChange | 一時停止値（要確定） |
| 再開 Resume | RequestStateChange | `2` (Enabled) |
| 状態保存 Save | RequestStateChange | 保存値（要確定） |
| 正常シャットダウン | `Msvm_ShutdownComponent.InitiateShutdown(Force,Reason)` | （メソッド方式・統合サービス必須） |

> **重要**: `Start`/`TurnOff` の `2`/`3` は確実。`Pause`/`Save` の数値は実装初期に
> 検証用 VM で実測・確定する（M3 のタスクに明記）。Core 層に定数として隠蔽する。

### 4.4 非同期ジョブの完了待ち
- `ReturnValue==4096` の場合、out パラメータ `Job`（`Msvm_ConcreteJob` 参照）を取得。
- `JobState` をポーリング（`7`=Completed、`10`=Exception など）し、完了/失敗を判定。
- 併せて呼び出し側で **状態ポーリング**（`Msvm_SummaryInformation.EnabledState` が目標値へ）
  ＋タイムアウト（既定 5 分、設定可能）で UI 完了判定を行う。

### 4.5 例外・エラー方針
- `CimException` を捕捉し、操作失敗としてメッセージ化（権限不足・統合サービス無効等を区別）。
- 正常シャットダウンが統合サービス無効で失敗した場合、UI で「強制停止」を案内。

---

## 5. Core 層 API 設計

```csharp
// Models/VmState.cs
public enum VmState { Unknown, Running, Off, Paused, Saved, Starting, Stopping, Saving, Pausing, Resuming, Other }

// Models/VmInfo.cs
public sealed record VmInfo(
    string Id, string Name, VmState State,
    int? CpuUsagePercent, long? MemoryUsageMb, TimeSpan? Uptime, string? Version);

// HyperV/IHyperVService.cs
public interface IHyperVService
{
    bool IsElevated { get; }
    ValueTask<IReadOnlyList<VmInfo>> GetVirtualMachinesAsync(CancellationToken ct = default);
    ValueTask StartAsync(string id, CancellationToken ct = default);
    ValueTask ShutdownAsync(string id, bool force, CancellationToken ct = default); // 正常終了
    ValueTask TurnOffAsync(string id, CancellationToken ct = default);              // 強制オフ
    ValueTask PauseAsync(string id, CancellationToken ct = default);
    ValueTask ResumeAsync(string id, CancellationToken ct = default);
    ValueTask SaveAsync(string id, CancellationToken ct = default);
}
```
- `HyperVService` はステートレス。`CimSession` は各操作内で `using` 生成。
- DI: `services.AddSingleton<IHyperVService, HyperVService>()`。
- CIM 同期 API はスレッドプールへ `Task.Run` でオフロードし UI スレッドを塞がない。

---

## 6. Web 層設計

- **Program.cs**: `AddRazorComponents().AddInteractiveServerComponents()`、`AddMudServices()`、
  `AddSingleton<IHyperVService,..>`、`AddScoped<ToastService>`。
- **VmListPage**: 参考 `Ec2Page` の `LoadAsync` / `RunOperationAsync` / `ConfirmAsync` を移植。
  - `MudTable` で一覧、`MudChip` で状態色分け、行アクションは `VmActionPolicy` で出し分け。
  - ヘッダーにホスト名・管理者権限バッジ（`IsElevated` が false なら警告色＋ツールチップ）。
  - 強制停止は `ConfirmDialog` で警告強調（データ損失の明示）。
- **共有部品**: `ConfirmDialog` / `ProgressOverlay` / `ToastService` を参考実装から移植。
- **VmActionPolicy**: `VmState` → 実行可能な操作集合・表示色・ラベルを集中管理。

---

## 7. 実装マイルストーン

各マイルストーンの完了条件に **「`dotnet build` 警告ゼロ」** を必ず含める。

### M0: ソリューション雛形
- `Monitor.Core`（クラスライブラリ, net10.0-windows）と `Monitor.Web`（Blazor Server）を作成。
- `Monitor.slnx` に追加、`Monitor.Web` → `Monitor.Core` 参照。MudBlazor 導入・初期表示。
- **DoD**: 既定ページがブラウザ表示され、ビルド警告ゼロ。

### M1: Core - VM 取得
- `VmInfo` / `VmState` / `CimConstants` / `VmStateMapper`、`HyperVService.GetVirtualMachinesAsync`。
- `Msvm_SummaryInformation` から取得（参考実装ロジック移植）。
- **DoD**: コンソール or 単体確認で VM 一覧（0 台環境では空リスト）が取得でき、例外なし。

### M2: Web - 一覧表示
- `VmListPage` 一覧表示、状態バッジ、更新ボタン、ホスト名・権限バッジ表示。
- **DoD**: ブラウザで一覧と手動更新が機能。0 台でも空表示で破綻しない。

### M3: Core - VM 操作
- Start/Shutdown(force)/TurnOff/Pause/Resume/Save を実装。`RequestStateChange`＋ジョブ待ち。
- `Pause`/`Save` の `RequestedState` 値を検証用 VM で実測・確定し定数化。
- **DoD**: 各操作 API が成功/失敗を正しく返す（検証用 VM で実機確認）。

### M4: Web - 操作 UI
- 行アクションボタン、`ConfirmDialog`（強制停止は警告強調）、`ProgressOverlay`、`ToastService`。
- 状態別の出し分け（`VmActionPolicy`）、操作後の一覧自動再取得。
- **DoD**: UI から各操作が実行でき、進行中表示・完了/失敗通知が動作。

### M5: 仕上げ
- エラーハンドリング統一、ロギング、`appsettings`（タイムアウト等）、`README` 更新。
- 非管理者起動時の警告導線、`docs` 最終化。
- **DoD**: 警告ゼロ、主要操作の一連動作確認、ドキュメント整合。

---

## 8. テスト・検証方針

- **スモーク**: 0 台環境（本開発機）で一覧取得・画面表示・例外なしを確認。
- **実機検証**: 検証用 VM を 1 台用意し、各状態遷移（開始/シャットダウン/強制/一時停止/再開/保存）を確認。
- **権限**: 非管理者起動で警告表示、管理者起動で操作成功を確認。
- Core はインターフェース分離済みのため、必要に応じ UI からの手動結合テストを主とする
  （CIM 依存のため自動単体テストは限定的。ロジック部＝状態マッピング等は単体テスト候補）。

---

## 9. リスクと対応

| リスク | 対応 |
|---|---|
| `Pause`/`Save` の状態値が不確実 | M3 で実測・確定し定数化。確定まで該当ボタンは無効化可 |
| 開発機に VM 0 台 | M0〜M2 は 0 台で進行可。M3/M4 で検証用 VM を用意 |
| 非管理者で操作失敗 | 起動時検知＋UI 警告、失敗を握りつぶさず通知 |
| 長時間ジョブのタイムアウト | ジョブ監視＋状態ポーリング＋設定可能タイムアウト |
| CIM 同期 API による UI ブロック | `Task.Run` オフロード＋操作中はボタン無効化 |

---

## 10. 実装着手順（推奨）

`M0 → M1 → M2 → M3 → M4 → M5`。各マイルストーン完了時にビルド警告ゼロを確認し、
区切りごとに動作を提示する。M3 で検証用 VM が必要になった時点でユーザーへ用意を依頼する。
