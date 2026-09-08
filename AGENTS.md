# Promptarium

- C# / .NET 8 / WPF / SQLite のローカル専用アプリを維持し、外部AI・外部サービスは追加しない。
- 元画像は読み取り専用とし、指定スキャンルート外を走査しない。rawメタデータとユーザー編集は別に保持し、再スキャンでユーザー編集を失わせない。画像・プロンプト・診断ログを外部送信しない。
- SQLiteスキーマ変更は非破壊マイグレーションにし、大量画像の一覧はページングまたは仮想化を維持する。
- ソース変更時は src/Promptarium/Promptarium.csproj の restore/build、テスト、SmokeTests を実行する。ローカルSQLite、bin/、obj/ はコミットしない。
