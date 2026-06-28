# hv-monitor

Windows ホスト上で動作する Hyper-V 仮想マシン（VM）の監視・操作を行う Blazor Server アプリケーションです。ブラウザから VM の一覧表示と、開始／停止／一時停止などの操作が行えます。

## 機能

- Hyper-V VM 一覧表示（名前・状態・CPU・メモリ・稼働時間・バージョン）
- VM 操作: 開始 / 再開 / 一時停止 / 状態保存 / シャットダウン（正常終了）/ 強制停止（電源オフ）
- 状態に応じた操作ボタンの出し分け、操作前の確認ダイアログ
- 手動更新、管理者権限の検知表示、操作の監査ログ出力

## 必要要件

- Windows 10/11 または Windows Server（Hyper-V の役割が有効）
- .NET 10 SDK
- VM の操作には **管理者権限** での実行が必要

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/Monitor.Web` | Blazor Server（UI、MudBlazor） |
| `src/Monitor.Core` | Hyper-V アクセス（CIM / Microsoft.Management.Infrastructure） |

## ビルド

```
dotnet build Monitor.slnx
```

## 実行

VM を操作する場合は、**管理者として** ターミナルを起動してから実行してください。

```
dotnet run --project src/Monitor.Web
```

起動後、表示される URL（既定: `http://localhost:5026`）にブラウザでアクセスします。
管理者権限が無い場合でも一覧表示は可能ですが、操作系は失敗します（画面に「非管理者」と表示されます）。

## 技術

- Blazor Server（.NET 10）/ MudBlazor
- Hyper-V アクセス: CIM（名前空間 `root\virtualization\v2`、`Msvm_*` クラス）
  - 一覧取得: `Msvm_SummaryInformation`
  - 状態変更: `Msvm_ComputerSystem.RequestStateChange`（非同期ジョブは `Msvm_ConcreteJob` を監視）
  - 正常シャットダウン: `Msvm_ShutdownComponent.InitiateShutdown`

## ドキュメント

- 仕様: [docs/spec-draft.md](docs/spec-draft.md)
- 実装プラン: [docs/implementation-plan.md](docs/implementation-plan.md)

## 注意

- 「強制停止」はゲスト OS の電源を即座に切るため、未保存データが失われる可能性があります。
- 「一時停止」「状態保存」など一部操作の状態コードは、実機 VM での最終確認を推奨します。
