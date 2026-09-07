# Promptarium

Promptarium は、ComfyUI で生成した PNG をローカルで整理・検索・再利用する Windows 専用アプリです。画像ファイルを変更せず、PNG 内の生成メタデータとユーザーの整理情報を SQLite カタログに分けて保存します。

## 主な機能

- 複数のスキャンフォルダを登録し、サブフォルダを再帰的に PNG スキャン
- ComfyUI の `prompt` JSON／`workflow` JSON と PNG テキストチャンクを原本として保存
- プロンプト、モデル、LoRA、VAE、Seed、Steps、CFG、Sampler、Scheduler、生成サイズを可能な範囲で抽出
- メタデータなし、破損、未知ノード、部分解析を区別して表示
- SHA-256 による完全重複の統合と、複数保存場所の表示
- 差分スキャン（同一パス・サイズ・更新日時のファイルは再読込と再ハッシュを省略）と完全再解析
- プロンプト、モデル、LoRA、タグ、カテゴリ、解析状態、評価、最小画像サイズの AND 検索
- お気に入り、1〜5 星評価、メモ、タグカテゴリ、手動補足値の保存
- プロンプト／カテゴリ単位のクリップボードコピー、workflow JSON 出力、手動バックアップ・復元
- ローカル診断ログの表示・コピー

## 必要環境

- Windows 10/11
- .NET 8 SDK
- インターネット接続（初回の NuGet 復元時のみ）

アプリは C#、.NET 8、WPF、SQLite、`Microsoft.Data.Sqlite` で構成されています。Docker、外部 AI API、外部サービス連携は使用しません。

## 実行

リポジトリのルートで実行します。

```powershell
dotnet restore src/Promptarium/Promptarium.csproj
dotnet run --project src/Promptarium/Promptarium.csproj
```

アプリで「フォルダを追加」を選び、画像を置いたフォルダを登録してください。登録したフォルダ以外は走査しません。通常の「全て再スキャン」は差分スキャンです。「完全再解析」は全対象を再度解析・ハッシュ化します。

## ビルドとテスト

clean checkoutでは、アプリと2つのtest projectを先にrestoreします。

```powershell
dotnet restore src/Promptarium/Promptarium.csproj
dotnet restore tests/Promptarium.Tests/Promptarium.Tests.csproj
dotnet restore tests/Promptarium.SmokeTests/Promptarium.SmokeTests.csproj
dotnet build src/Promptarium/Promptarium.csproj --no-restore
dotnet test tests/Promptarium.Tests/Promptarium.Tests.csproj --no-restore
dotnet run --project tests/Promptarium.SmokeTests/Promptarium.SmokeTests.csproj --no-restore
```

xUnit テストは解析、情報源、SQLite 保存・検索、差分／完全再解析を確認します。スモークテストは最小 PNG／ComfyUI JSON フィクスチャを使い、メタデータ抽出、重複統合、編集保持、バックアップなどを通しで検証します。

## 保存先とプライバシー

アプリのデータは次のローカルフォルダに保存されます。

```text
%LOCALAPPDATA%\Promptarium\
  promptarium.db       SQLite カタログ
  backups\             手動バックアップの初期保存先
  logs\promptarium.log 診断ログ
```

元画像は読み取り専用で扱います。Promptarium が画像を削除、移動、改名、再エンコードすることはありません。画像・プロンプト・メタデータを外部へ送信する機能もありません。

バックアップ時には保存ダイアログで別の場所も選択できます。`backups` は初期表示される既定フォルダであり、実際の保存先は選択したpathです。復元は現在のカタログを置き換える操作なので、復元前に現状のバックアップも作成してください。

## Windows向け発行

64-bit Windows向けの自己完結・単一ファイル版を作る例です。

```powershell
dotnet publish src/Promptarium/Promptarium.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

出力先は `src/Promptarium/bin/Release/net8.0-windows/win-x64/publish/` です。配布前に、そのフォルダの `Promptarium.exe` を配布対象Windowsで起動確認してください。

## 対応範囲と制約

- PNG と ComfyUI メタデータが対象です。JPEG、WebP、AVIF、Stable Diffusion WebUI、Forge は対象外です。
- `workflow` JSON のみの解析は標準 UI 構成に対するベストエフォートであり、「一部解析」と表示されます。
- カスタムノードは画像登録を止めず、未知ノードとして明示表示します。
- 常時フォルダ監視、複数 PC 同期、類似画像検索、AI 分類は実装していません。
- 実ComfyUI PNGを用いたカスタムノード検証と10,000件規模の定量性能測定は今後の課題です。

実装状況と既知の制約は [docs/implementation-status.md](docs/implementation-status.md)、要件の正本は [docs/requirements.md](docs/requirements.md) を参照してください。

## 構成

```text
src/Promptarium/              WPF アプリ
  Services/                   PNG抽出、ComfyUI解析、SQLite、スキャン、診断
  Models/                     ドメインモデル
tests/Promptarium.Tests/      xUnit 回帰テスト
tests/Promptarium.SmokeTests/ 実行可能なスモークテストとフィクスチャ
docs/                         要件、計画、進捗台帳
```
