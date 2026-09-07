# Promptarium リポジトリガイド

## プロジェクトの前提

- 技術構成は **C# / .NET 8 / WPF / SQLite / `Microsoft.Data.Sqlite`** を維持する。
- Windows のローカル専用アプリである。Docker、Tauri、Electron、Python、外部 AI API、外部サービス連携を導入しない。
- 要件の正本は [docs/requirements.md](docs/requirements.md)、進捗の正本は [docs/implementation-status.md](docs/implementation-status.md) とする。

## 守るべき不変条件

- 元画像は読み取り対象であり、削除、移動、改名、再エンコード、上書きをしない。
- 指定されたスキャンルート以外を走査しない。
- PNG の raw メタデータ、`prompt` JSON、`workflow` JSON は原本として保持する。
- 自動抽出値、workflow からの導出値、ユーザー入力を別に保存・表示する。再スキャンでユーザー編集を失わせない。
- 解析不能、破損メタデータ、メタデータなし、未知ノードで画像全体の登録を失敗させない。
- 画像、プロンプト、メタデータ、診断ログを外部へ送信しない。

## 作業開始時

機能変更を始める前に、次を確認する。

```powershell
git status --short
Get-Content -Raw docs/requirements.md
Get-Content -Raw docs/implementation-plan.md
Get-Content -Raw docs/implementation-status.md
rg --files src/Promptarium
Get-Content -Raw NuGet.Config
```

既存の未コミット変更は、ユーザーが承認済みの成果物として扱う。無関係な変更を削除・巻き戻し・再作成しない。

## 実装方針

- データモデル・SQLiteスキーマの変更は非破壊のマイグレーションにする。既存カタログを削除して作り直さない。
- スキャンは差分を優先する。同一パス・サイズ・更新日時のファイルは再読込と完全ハッシュを省略し、変更または移動候補のみ完全ハッシュで照合する。
- 大量画像のUIはページングまたは仮想化を維持する。全件のサムネイルを同時に描画しない。
- 例外は利用者に曖昧なエラーだけを出さず、`AppLogger` を通してローカル診断ログへ記録する。
- ComfyUI解析を拡張する際は、標準ノードを既知として追加し、カスタムノードは未知ノードとして理由を表示する。

## 検証コマンド

このリポジトリにはソリューションファイルがないため、プロジェクトを明示して実行する。

```powershell
dotnet restore src/Promptarium/Promptarium.csproj
dotnet build src/Promptarium/Promptarium.csproj --no-restore
dotnet test tests/Promptarium.Tests/Promptarium.Tests.csproj --no-restore
dotnet run --project tests/Promptarium.SmokeTests/Promptarium.SmokeTests.csproj --no-restore
```

WPFの変更後は、可能なら `dotnet run --project src/Promptarium/Promptarium.csproj` で起動継続も確認する。実PNGがない場合は、最小PNGとJSONフィクスチャで検証する。

## 進捗文書

- 作業対象のマイルストーンは開始時に `[-]` にする。
- ビルド・テストで確認できた項目だけ `[x]` にする。
- 未完了、既知の制約、失敗した検証は `docs/implementation-status.md` に残す。
- 計画の重要な変更は `docs/implementation-plan.md` に反映する。
- 文書を変更する前に、更新内容を短く説明する。

## Git

- コミットや push は、ユーザーが明示的に依頼したときだけ行う。
- コミット前に `git diff --check` とステージ済み差分を確認する。
- 無関係なファイル、ローカルSQLite、`bin`、`obj` はコミットしない。
