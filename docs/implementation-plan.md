# Hyper-V VM モニター 実装プラン（分散構成 v2）

> 対象設計: `docs/architecture.md`（確定） / 更新: 2026-06-28
> 確定事項: REST/Minimal API・APIキー認証・複数ホストは 1 表統合
> 注: 旧モノリス版プランを本書で置き換える。既存の `Monitor.Core` / UI 部品は流用する。

---

## 1. 最終プロジェクト構成

```
Monitor.slnx
├─ src/
│  ├─ Monitor.Contracts/   net10.0          DTO・API契約（参照なし）
│  ├─ Monitor.Core/        net10.0-windows  CIM(HyperVService)  →参照: Contracts
│  ├─ Monitor.Agent/       net10.0-windows  Minimal API         →参照: Core, Contracts
│  └─ Monitor.Web/         net10.0          Blazor Server       →参照: Contracts
```

## 2. 依存パッケージ

| プロジェクト | パッケージ |
|---|---|
| Monitor.Contracts | （なし） |
| Monitor.Core | Microsoft.Management.Infrastructure（既存） |
| Monitor.Agent | ASP.NET Core（Sdk.Web 標準。Minimal API） |
| Monitor.Web | MudBlazor（既存）。HttpClient は標準 |

---

## 3. マイルストーン

各マイルストーンの完了条件に **「`dotnet build` 警告ゼロ」** を含める。

### V0: 契約抽出（Contracts 新設）
- `Monitor.Contracts`（net10.0 classlib）を作成し `Monitor.slnx` に追加。
- `VmInfo` / `VmState` を `Monitor.Core.Models` → `Monitor.Contracts` へ移動。
- `HostInfo(string Name, bool IsElevated)` / `ShutdownRequest(bool Force)` を追加。
- `Monitor.Core` が `Monitor.Contracts` を参照、`HyperVService` 等の using を更新。
- `Monitor.Web` は暫定的に現状維持（Core 参照のまま。V3 で切替）。
- **DoD**: ソリューション全体がビルド警告ゼロ。既存の一覧画面が従来どおり起動。

### V1: Monitor.Agent（API サーバ）
- `Monitor.Agent`（net10.0-windows, Sdk.Web）を作成、`Core` / `Contracts` 参照。
- `Program.cs`: `AddSingleton<IHyperVService, HyperVService>()`、API キー検証（エンドポイントフィルタ or ミドルウェア）。
- Minimal API エンドポイント（health / host / vms / 各操作）を実装。エラーは `ProblemDetails`。
- `appsettings.json`: `Agent:ApiKey`、Kestrel ポート（例 5100）。
- **DoD**: Agent 単体で起動し、`GET /api/health`=200、`GET /api/vms`（X-Api-Key 付き）=200 で空配列、キー無し=401。0 台環境でこのホストで確認。

### V2: Web の API クライアントと設定
- `HyperVHostsOptions`（`HyperVHosts[]`: Name/BaseUrl/ApiKey）を Options で読み込み。
- `IHyperVApiClient` / `HyperVApiClient`（`IHttpClientFactory`、`X-Api-Key` 付与、Timeout 延長）。
- 取得は全ホスト並列、`HostVmResult(HostName, Vms, Error)` を返す（部分失敗許容）。
- **DoD**: 単体（or 簡易ページ）で、設定したホストから一覧取得できる（ビルド警告ゼロ）。

### V3: Web 画面の複数ホスト統合表示
- `VmListPage` を改修: 全ホスト並列取得 → 1 表統合（**ホスト列**追加）、ホストフィルタ、手動更新。
- 到達不可ホストは画面上部にエラー表示（当該ホストの VM は除外）。
- ホストごとの `isElevated` 表示。
- `Monitor.Web` の **Core 参照を廃止**し Contracts 参照に、TFM を `net10.0` へ。
- **DoD**: このホストで Agent を 1 台起動 → Web に登録 → 一覧が 1 表表示（0 台で空）。到達不可ホストでエラー表示。

### V4: 操作 UI の複数ホスト対応
- 行アクション（開始/再開/一時停止/保存/シャットダウン/強制停止）を、対象 VM の**ホストの Agent**へ送るよう実装。
- 既存の `ConfirmDialog` / `ProgressOverlay` / `ToastService` を流用。操作後は当該ホスト（or 全体）を再取得。
- **DoD**: UI から操作 API が呼ばれ、進捗・成否通知が動作（実 VM 操作の最終確認は検証用 VM）。

### V5: 仕上げ
- 設定サンプル（`appsettings.Sample.json`）を Agent/Web に追加。README を分散構成へ更新（2 プロセスの起動手順、設定例、権限）。
- HTTPS/証明書は **後回し（2026-06-28 決定）**。現状は HTTP で運用し、API キーは信頼ネットワーク前提。本番は HTTPS + 正規証明書を別途導入。
- エラー処理・ロギング整備、`docs` 最終化。
- **DoD**: 警告ゼロ、Agent↔Web 疎通確認、ドキュメント整合（HTTPS は対象外）。

---

## 4. テスト・検証方針

- **V1**: このホストで Agent を起動し、`curl`/PowerShell で health/host/vms と 401 を確認（0 台で空配列）。
- **V3**: Agent 1 台 + Web を起動し、ブラウザで 1 表統合表示・到達不可エラーを確認。
- **V4 実機**: 検証用 VM を 1 台用意し、UI からの各操作と状態遷移を確認。
- 各段でビルド警告ゼロを必須。

---

## 5. 移行順序（既存資産の扱い）

`V0(契約) → V1(Agent) → V2(クライアント) → V3(画面統合) → V4(操作) → V5(仕上げ)`

- `Monitor.Core` の CIM 実装と UI 部品はそのまま流用。
- DTO 移動（V0）→ Agent 追加（V1）→ Web 切替（V3）の順で、各段ビルドを壊さず移行する。
- 検証用 VM が必要になるのは V4 の実機操作確認のみ。V1〜V3 は 0 台で進行可能。
