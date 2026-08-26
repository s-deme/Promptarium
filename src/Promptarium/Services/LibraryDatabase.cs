using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using Promptarium.Models;

namespace Promptarium.Services;

public sealed class LibraryDatabase
{
    private readonly string _databasePath;

    public LibraryDatabase(string? databasePath = null)
    {
        _databasePath = databasePath ?? AppPaths.DatabasePath;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS scan_roots (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                include_subfolders INTEGER NOT NULL DEFAULT 1,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                last_scanned_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS image_assets (
                id INTEGER PRIMARY KEY,
                content_hash TEXT NOT NULL UNIQUE,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS file_locations (
                id INTEGER PRIMARY KEY,
                asset_id INTEGER NOT NULL REFERENCES image_assets(id) ON DELETE CASCADE,
                root_id INTEGER NULL REFERENCES scan_roots(id) ON DELETE SET NULL,
                path TEXT NOT NULL UNIQUE,
                relative_path TEXT NULL,
                file_size INTEGER NOT NULL,
                last_write_utc TEXT NOT NULL,
                availability INTEGER NOT NULL DEFAULT 1,
                last_seen_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_file_locations_asset ON file_locations(asset_id);
            CREATE INDEX IF NOT EXISTS ix_file_locations_root_seen ON file_locations(root_id, last_seen_utc);
            CREATE TABLE IF NOT EXISTS generation_records (
                asset_id INTEGER PRIMARY KEY REFERENCES image_assets(id) ON DELETE CASCADE,
                raw_metadata TEXT NULL,
                prompt_json TEXT NULL,
                workflow_json TEXT NULL,
                positive_prompt TEXT NULL,
                negative_prompt TEXT NULL,
                model_name TEXT NULL,
                loras_json TEXT NULL,
                vae_name TEXT NULL,
                seed TEXT NULL,
                steps TEXT NULL,
                cfg TEXT NULL,
                sampler TEXT NULL,
                scheduler TEXT NULL,
                parse_status TEXT NOT NULL,
                parse_message TEXT NULL,
                value_source TEXT NULL,
                parser_version TEXT NOT NULL,
                parsed_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_edits (
                asset_id INTEGER PRIMARY KEY REFERENCES image_assets(id) ON DELETE CASCADE,
                positive_prompt TEXT NULL,
                negative_prompt TEXT NULL,
                model_name TEXT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                rating INTEGER NULL CHECK(rating IS NULL OR rating BETWEEN 1 AND 5),
                note TEXT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_generation_overrides (
                asset_id INTEGER PRIMARY KEY REFERENCES image_assets(id) ON DELETE CASCADE,
                vae_name TEXT NULL,
                seed TEXT NULL,
                steps TEXT NULL,
                cfg TEXT NULL,
                sampler TEXT NULL,
                scheduler TEXT NULL,
                width TEXT NULL,
                height TEXT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prompt_tags (
                id INTEGER PRIMARY KEY,
                asset_id INTEGER NOT NULL REFERENCES image_assets(id) ON DELETE CASCADE,
                prompt_kind TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                raw_text TEXT NOT NULL,
                normalized_text TEXT NOT NULL,
                category TEXT NOT NULL,
                source TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_prompt_tags_asset ON prompt_tags(asset_id);
            CREATE INDEX IF NOT EXISTS ix_prompt_tags_category ON prompt_tags(category);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "generation_records", "value_source TEXT NULL");
    }

    public long UpsertScanRoot(string path, bool includeSubfolders = true)
    {
        var normalized = Path.GetFullPath(path);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_roots(path, include_subfolders, is_enabled, created_utc)
            VALUES($path, $recursive, 1, $now)
            ON CONFLICT(path) DO UPDATE SET include_subfolders = excluded.include_subfolders, is_enabled = 1;
            SELECT id FROM scan_roots WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$recursive", includeSubfolders ? 1 : 0);
        command.Parameters.AddWithValue("$now", UtcNow());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public IReadOnlyList<ScanRoot> GetScanRoots()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, include_subfolders, is_enabled FROM scan_roots WHERE is_enabled = 1 ORDER BY path";
        using var reader = command.ExecuteReader();
        var roots = new List<ScanRoot>();
        while (reader.Read())
        {
            roots.Add(new ScanRoot
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                IncludeSubfolders = reader.GetInt64(2) == 1,
                IsEnabled = reader.GetInt64(3) == 1
            });
        }

        return roots;
    }

    public long UpsertImage(long rootId, string fullPath, FileInfo file, string hash, PngMetadata metadata, ParsedGeneration generation, IReadOnlyList<PromptTag> automaticTags, DateTime scanStartedUtc)
    {
        var now = UtcNow();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var assetId = GetOrCreateAsset(connection, transaction, hash, metadata.Width, metadata.Height, now);
        UpsertLocation(connection, transaction, rootId, fullPath, file, assetId, scanStartedUtc);
        UpsertGeneration(connection, transaction, assetId, metadata, generation, now);
        ReplaceAutomaticTags(connection, transaction, assetId, automaticTags);
        transaction.Commit();
        return assetId;
    }

    public void MarkMissingLocations(long rootId, DateTime scanStartedUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE file_locations
            SET availability = 0
            WHERE root_id = $rootId AND last_seen_utc < $started;
            UPDATE scan_roots SET last_scanned_utc = $started WHERE id = $rootId;
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$started", scanStartedUtc.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ImageSummary> Search(string? text, string? model, string? lora, string? tag, string? category, bool favoritesOnly, int limit, int offset)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.content_hash, l.path, l.file_size, l.last_write_utc, a.width, a.height,
                   g.positive_prompt, g.negative_prompt, g.model_name, g.loras_json, g.parse_status,
                   COALESCE(u.is_favorite, 0), u.rating, u.note,
                   (SELECT COUNT(*) FROM file_locations lc WHERE lc.asset_id = a.id AND lc.availability = 1) AS location_count
            FROM image_assets a
            JOIN file_locations l ON l.id = (
                SELECT li.id FROM file_locations li
                WHERE li.asset_id = a.id AND li.availability = 1
                ORDER BY li.last_write_utc DESC, li.id ASC LIMIT 1)
            LEFT JOIN generation_records g ON g.asset_id = a.id
            LEFT JOIN user_edits u ON u.asset_id = a.id
            WHERE ($favoritesOnly = 0 OR COALESCE(u.is_favorite, 0) = 1)
              AND ($text = '' OR lower(COALESCE(u.positive_prompt, g.positive_prompt, '') || ' ' || COALESCE(u.negative_prompt, g.negative_prompt, '') || ' ' || COALESCE(u.model_name, g.model_name, '') || ' ' || COALESCE(u.note, '')) LIKE '%' || lower($text) || '%')
              AND ($model = '' OR lower(COALESCE(u.model_name, g.model_name, '')) LIKE '%' || lower($model) || '%')
              AND ($lora = '' OR lower(COALESCE(g.loras_json, '')) LIKE '%' || lower($lora) || '%')
              AND ($tag = '' OR EXISTS (SELECT 1 FROM prompt_tags t WHERE t.asset_id = a.id AND lower(t.normalized_text) LIKE '%' || lower($tag) || '%'))
              AND ($category = '' OR EXISTS (SELECT 1 FROM prompt_tags tc WHERE tc.asset_id = a.id AND tc.category = $category))
            ORDER BY l.last_write_utc DESC, a.id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$favoritesOnly", favoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("$text", text?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$model", model?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$lora", lora?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$tag", tag?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$category", category?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var records = new List<ImageSummary>();
        while (reader.Read()) records.Add(ReadSummary(reader));
        return records;
    }

    public int CountSearchResults(string? text, string? model, string? lora, string? tag, string? category, bool favoritesOnly)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM image_assets a
            WHERE EXISTS (SELECT 1 FROM file_locations l WHERE l.asset_id = a.id AND l.availability = 1)
              AND ($favoritesOnly = 0 OR COALESCE((SELECT is_favorite FROM user_edits u WHERE u.asset_id = a.id), 0) = 1)
              AND ($text = '' OR lower(COALESCE((SELECT positive_prompt FROM user_edits u WHERE u.asset_id = a.id), (SELECT positive_prompt FROM generation_records g WHERE g.asset_id = a.id), '') || ' ' || COALESCE((SELECT negative_prompt FROM user_edits u WHERE u.asset_id = a.id), (SELECT negative_prompt FROM generation_records g WHERE g.asset_id = a.id), '') || ' ' || COALESCE((SELECT model_name FROM user_edits u WHERE u.asset_id = a.id), (SELECT model_name FROM generation_records g WHERE g.asset_id = a.id), '') || ' ' || COALESCE((SELECT note FROM user_edits u WHERE u.asset_id = a.id), '')) LIKE '%' || lower($text) || '%')
              AND ($model = '' OR lower(COALESCE((SELECT model_name FROM user_edits u WHERE u.asset_id = a.id), (SELECT model_name FROM generation_records g WHERE g.asset_id = a.id), '')) LIKE '%' || lower($model) || '%')
              AND ($lora = '' OR lower(COALESCE((SELECT loras_json FROM generation_records g WHERE g.asset_id = a.id), '')) LIKE '%' || lower($lora) || '%')
              AND ($tag = '' OR EXISTS (SELECT 1 FROM prompt_tags t WHERE t.asset_id = a.id AND lower(t.normalized_text) LIKE '%' || lower($tag) || '%'))
              AND ($category = '' OR EXISTS (SELECT 1 FROM prompt_tags tc WHERE tc.asset_id = a.id AND tc.category = $category));
            """;
        AddSearchParameters(command, text, model, lora, tag, category, favoritesOnly);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public ImageDetail? GetDetail(long assetId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.content_hash,
                   (SELECT l.path FROM file_locations l WHERE l.asset_id = a.id AND l.availability = 1 ORDER BY l.last_write_utc DESC LIMIT 1),
                   COALESCE((SELECT l.file_size FROM file_locations l WHERE l.asset_id = a.id AND l.availability = 1 ORDER BY l.last_write_utc DESC LIMIT 1), 0),
                   COALESCE((SELECT l.last_write_utc FROM file_locations l WHERE l.asset_id = a.id AND l.availability = 1 ORDER BY l.last_write_utc DESC LIMIT 1), ''),
                   a.width, a.height, g.positive_prompt, g.negative_prompt, g.model_name, g.loras_json, g.parse_status,
                   COALESCE(u.is_favorite, 0), u.rating, u.note,
                   (SELECT COUNT(*) FROM file_locations lc WHERE lc.asset_id = a.id AND lc.availability = 1),
                   g.raw_metadata, g.prompt_json, g.workflow_json, g.vae_name, g.seed, g.steps, g.cfg, g.sampler, g.scheduler, g.parse_message, g.value_source,
                   u.positive_prompt, u.negative_prompt, u.model_name,
                   o.vae_name, o.seed, o.steps, o.cfg, o.sampler, o.scheduler, o.width, o.height
            FROM image_assets a
            LEFT JOIN generation_records g ON g.asset_id = a.id
            LEFT JOIN user_edits u ON u.asset_id = a.id
            LEFT JOIN user_generation_overrides o ON o.asset_id = a.id
            WHERE a.id = $id;
            """;
        command.Parameters.AddWithValue("$id", assetId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var detail = new ImageDetail
        {
            Id = reader.GetInt64(0), ContentHash = reader.GetString(1), PrimaryPath = GetString(reader, 2) ?? string.Empty,
            FileSize = reader.GetInt64(3), LastWriteUtc = ParseUtc(GetString(reader, 4)), Width = reader.GetInt32(5), Height = reader.GetInt32(6),
            PositivePrompt = GetString(reader, 7), NegativePrompt = GetString(reader, 8), ModelName = GetString(reader, 9), LorasJson = GetString(reader, 10),
            ParseStatus = ParseStatusValue(GetString(reader, 11)), IsFavorite = reader.GetInt64(12) == 1, Rating = GetNullableInt(reader, 13), Note = GetString(reader, 14), LocationCount = reader.GetInt32(15),
            RawMetadata = GetString(reader, 16), PromptJson = GetString(reader, 17), WorkflowJson = GetString(reader, 18), VaeName = GetString(reader, 19),
            Seed = GetString(reader, 20), Steps = GetString(reader, 21), Cfg = GetString(reader, 22), Sampler = GetString(reader, 23), Scheduler = GetString(reader, 24),
            ParseMessage = GetString(reader, 25), ParseSource = GetString(reader, 26), ManualPositivePrompt = GetString(reader, 27), ManualNegativePrompt = GetString(reader, 28), ManualModelName = GetString(reader, 29),
            ManualVaeName = GetString(reader, 30), ManualSeed = GetString(reader, 31), ManualSteps = GetString(reader, 32), ManualCfg = GetString(reader, 33),
            ManualSampler = GetString(reader, 34), ManualScheduler = GetString(reader, 35), ManualWidth = GetString(reader, 36), ManualHeight = GetString(reader, 37)
        };
        reader.Close();

        using var locationCommand = connection.CreateCommand();
        locationCommand.CommandText = "SELECT path, availability FROM file_locations WHERE asset_id = $id ORDER BY availability DESC, path";
        locationCommand.Parameters.AddWithValue("$id", assetId);
        using var locations = locationCommand.ExecuteReader();
        while (locations.Read())
        {
            var path = locations.GetString(0);
            detail.Locations.Add(locations.GetInt64(1) == 1 ? path : $"{path}（所在不明）");
        }

        using var tagCommand = connection.CreateCommand();
        tagCommand.CommandText = "SELECT id, prompt_kind, ordinal, raw_text, normalized_text, category, source FROM prompt_tags WHERE asset_id = $id ORDER BY prompt_kind, ordinal, id";
        tagCommand.Parameters.AddWithValue("$id", assetId);
        using var tags = tagCommand.ExecuteReader();
        while (tags.Read())
        {
            detail.Tags.Add(new PromptTag { Id = tags.GetInt64(0), PromptKind = tags.GetString(1), Ordinal = tags.GetInt32(2), RawText = tags.GetString(3), NormalizedText = tags.GetString(4), Category = tags.GetString(5), Source = tags.GetString(6) });
        }

        return detail;
    }

    public void SaveUserEdits(long assetId, UserEdits edits, UserGenerationOverrides overrides, IReadOnlyList<PromptTag> tags)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO user_edits(asset_id, positive_prompt, negative_prompt, model_name, is_favorite, rating, note, updated_utc)
                VALUES($id, $positive, $negative, $model, $favorite, $rating, $note, $updated)
                ON CONFLICT(asset_id) DO UPDATE SET
                    positive_prompt = excluded.positive_prompt, negative_prompt = excluded.negative_prompt,
                    model_name = excluded.model_name, is_favorite = excluded.is_favorite, rating = excluded.rating,
                    note = excluded.note, updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", assetId);
            command.Parameters.AddWithValue("$positive", DbValue(edits.PositivePrompt));
            command.Parameters.AddWithValue("$negative", DbValue(edits.NegativePrompt));
            command.Parameters.AddWithValue("$model", DbValue(edits.ModelName));
            command.Parameters.AddWithValue("$favorite", edits.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("$rating", edits.Rating is { } rating ? rating : DBNull.Value);
            command.Parameters.AddWithValue("$note", DbValue(edits.Note));
            command.Parameters.AddWithValue("$updated", UtcNow());
            command.ExecuteNonQuery();
        }

        foreach (var tag in tags.Where(tag => tag.Id > 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE prompt_tags SET category = $category, source = CASE WHEN source = 'manual' OR category <> $category THEN 'manual' ELSE source END WHERE id = $id AND asset_id = $assetId";
            command.Parameters.AddWithValue("$category", tag.Category);
            command.Parameters.AddWithValue("$id", tag.Id);
            command.Parameters.AddWithValue("$assetId", assetId);
            command.ExecuteNonQuery();
        }

        SaveGenerationOverrides(connection, transaction, assetId, overrides);

        transaction.Commit();
    }

    public IReadOnlyList<string> GetModels()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT model_name
            FROM (
                SELECT model_name FROM generation_records
                UNION
                SELECT model_name FROM user_edits
            )
            WHERE model_name IS NOT NULL AND model_name <> ''
            ORDER BY model_name;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public IReadOnlyList<string> GetLoras()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT loras_json FROM generation_records WHERE loras_json IS NOT NULL AND loras_json <> ''";
        using var reader = command.ExecuteReader();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            try
            {
                var loras = JsonSerializer.Deserialize<List<LoraUsage>>(reader.GetString(0)) ?? [];
                foreach (var lora in loras) names.Add(lora.Name);
            }
            catch (JsonException)
            {
                // A malformed historical record is kept as raw data but excluded from this filter list.
            }
        }

        return names.ToList();
    }

    public void BackupTo(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = OpenConnection();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }

    public void RestoreFrom(string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("バックアップファイルが見つかりません。", source);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        File.Copy(source, _databasePath, overwrite: true);
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long GetOrCreateAsset(SqliteConnection connection, SqliteTransaction transaction, string hash, int width, int height, string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO image_assets(content_hash, width, height, first_seen_utc, last_seen_utc)
            VALUES($hash, $width, $height, $now, $now)
            ON CONFLICT(content_hash) DO UPDATE SET width = excluded.width, height = excluded.height, last_seen_utc = excluded.last_seen_utc;
            SELECT id FROM image_assets WHERE content_hash = $hash;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void UpsertLocation(SqliteConnection connection, SqliteTransaction transaction, long rootId, string fullPath, FileInfo file, long assetId, DateTime scanStartedUtc)
    {
        using var rootCommand = connection.CreateCommand();
        rootCommand.Transaction = transaction;
        rootCommand.CommandText = "SELECT path FROM scan_roots WHERE id = $rootId";
        rootCommand.Parameters.AddWithValue("$rootId", rootId);
        var rootPath = Convert.ToString(rootCommand.ExecuteScalar()) ?? string.Empty;
        var relativePath = Path.GetRelativePath(rootPath, fullPath);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_locations(asset_id, root_id, path, relative_path, file_size, last_write_utc, availability, last_seen_utc)
            VALUES($assetId, $rootId, $path, $relativePath, $fileSize, $lastWrite, 1, $lastSeen)
            ON CONFLICT(path) DO UPDATE SET
                asset_id = excluded.asset_id, root_id = excluded.root_id, relative_path = excluded.relative_path,
                file_size = excluded.file_size, last_write_utc = excluded.last_write_utc, availability = 1, last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$assetId", assetId);
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$fileSize", file.Length);
        command.Parameters.AddWithValue("$lastWrite", file.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$lastSeen", scanStartedUtc.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertGeneration(SqliteConnection connection, SqliteTransaction transaction, long assetId, PngMetadata metadata, ParsedGeneration generation, string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO generation_records(asset_id, raw_metadata, prompt_json, workflow_json, positive_prompt, negative_prompt, model_name, loras_json, vae_name, seed, steps, cfg, sampler, scheduler, parse_status, parse_message, value_source, parser_version, parsed_utc)
            VALUES($assetId, $rawMetadata, $prompt, $workflow, $positive, $negative, $model, $loras, $vae, $seed, $steps, $cfg, $sampler, $scheduler, $status, $message, $valueSource, $version, $now)
            ON CONFLICT(asset_id) DO UPDATE SET
                raw_metadata = excluded.raw_metadata, prompt_json = excluded.prompt_json, workflow_json = excluded.workflow_json,
                positive_prompt = excluded.positive_prompt, negative_prompt = excluded.negative_prompt, model_name = excluded.model_name,
                loras_json = excluded.loras_json, vae_name = excluded.vae_name, seed = excluded.seed, steps = excluded.steps,
                cfg = excluded.cfg, sampler = excluded.sampler, scheduler = excluded.scheduler, parse_status = excluded.parse_status,
                parse_message = excluded.parse_message, value_source = excluded.value_source, parser_version = excluded.parser_version, parsed_utc = excluded.parsed_utc;
            """;
        command.Parameters.AddWithValue("$assetId", assetId);
        command.Parameters.AddWithValue("$rawMetadata", JsonSerializer.Serialize(metadata.Text));
        command.Parameters.AddWithValue("$prompt", DbValue(metadata.Text.GetValueOrDefault("prompt")));
        command.Parameters.AddWithValue("$workflow", DbValue(metadata.Text.GetValueOrDefault("workflow")));
        command.Parameters.AddWithValue("$positive", DbValue(generation.PositivePrompt));
        command.Parameters.AddWithValue("$negative", DbValue(generation.NegativePrompt));
        command.Parameters.AddWithValue("$model", DbValue(generation.ModelName));
        command.Parameters.AddWithValue("$loras", JsonSerializer.Serialize(generation.Loras));
        command.Parameters.AddWithValue("$vae", DbValue(generation.VaeName));
        command.Parameters.AddWithValue("$seed", DbValue(generation.Seed));
        command.Parameters.AddWithValue("$steps", DbValue(generation.Steps));
        command.Parameters.AddWithValue("$cfg", DbValue(generation.Cfg));
        command.Parameters.AddWithValue("$sampler", DbValue(generation.Sampler));
        command.Parameters.AddWithValue("$scheduler", DbValue(generation.Scheduler));
        command.Parameters.AddWithValue("$status", generation.Status.ToString());
        command.Parameters.AddWithValue("$message", DbValue(generation.Message));
        command.Parameters.AddWithValue("$valueSource", DbValue(generation.ValueSource));
        command.Parameters.AddWithValue("$version", ComfyWorkflowParser.Version);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void ReplaceAutomaticTags(SqliteConnection connection, SqliteTransaction transaction, long assetId, IReadOnlyList<PromptTag> tags)
    {
        var manualCategories = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT prompt_kind, normalized_text, category FROM prompt_tags WHERE asset_id = $assetId AND source = 'manual'";
            select.Parameters.AddWithValue("$assetId", assetId);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                manualCategories[TagKey(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM prompt_tags WHERE asset_id = $assetId";
            delete.Parameters.AddWithValue("$assetId", assetId);
            delete.ExecuteNonQuery();
        }

        foreach (var tag in tags)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO prompt_tags(asset_id, prompt_kind, ordinal, raw_text, normalized_text, category, source) VALUES($assetId, $kind, $ordinal, $raw, $normalized, $category, 'automatic')";
            insert.Parameters.AddWithValue("$assetId", assetId);
            insert.Parameters.AddWithValue("$kind", tag.PromptKind);
            insert.Parameters.AddWithValue("$ordinal", tag.Ordinal);
            insert.Parameters.AddWithValue("$raw", tag.RawText);
            insert.Parameters.AddWithValue("$normalized", tag.NormalizedText);
            var hasManualCategory = manualCategories.TryGetValue(TagKey(tag.PromptKind, tag.NormalizedText), out var manualCategory);
            insert.Parameters.AddWithValue("$category", hasManualCategory ? manualCategory : tag.Category);
            insert.CommandText = "INSERT INTO prompt_tags(asset_id, prompt_kind, ordinal, raw_text, normalized_text, category, source) VALUES($assetId, $kind, $ordinal, $raw, $normalized, $category, $source)";
            insert.Parameters.AddWithValue("$source", hasManualCategory ? "manual" : "automatic");
            insert.ExecuteNonQuery();
        }
    }

    private static void SaveGenerationOverrides(SqliteConnection connection, SqliteTransaction transaction, long assetId, UserGenerationOverrides overrides)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (overrides.IsEmpty)
        {
            command.CommandText = "DELETE FROM user_generation_overrides WHERE asset_id = $id";
            command.Parameters.AddWithValue("$id", assetId);
            command.ExecuteNonQuery();
            return;
        }

        command.CommandText = """
            INSERT INTO user_generation_overrides(asset_id, vae_name, seed, steps, cfg, sampler, scheduler, width, height, updated_utc)
            VALUES($id, $vae, $seed, $steps, $cfg, $sampler, $scheduler, $width, $height, $updated)
            ON CONFLICT(asset_id) DO UPDATE SET
                vae_name = excluded.vae_name, seed = excluded.seed, steps = excluded.steps, cfg = excluded.cfg,
                sampler = excluded.sampler, scheduler = excluded.scheduler, width = excluded.width, height = excluded.height,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", assetId);
        command.Parameters.AddWithValue("$vae", DbValue(overrides.VaeName));
        command.Parameters.AddWithValue("$seed", DbValue(overrides.Seed));
        command.Parameters.AddWithValue("$steps", DbValue(overrides.Steps));
        command.Parameters.AddWithValue("$cfg", DbValue(overrides.Cfg));
        command.Parameters.AddWithValue("$sampler", DbValue(overrides.Sampler));
        command.Parameters.AddWithValue("$scheduler", DbValue(overrides.Scheduler));
        command.Parameters.AddWithValue("$width", DbValue(overrides.Width));
        command.Parameters.AddWithValue("$height", DbValue(overrides.Height));
        command.Parameters.AddWithValue("$updated", UtcNow());
        command.ExecuteNonQuery();
    }

    private static ImageSummary ReadSummary(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0), ContentHash = reader.GetString(1), PrimaryPath = reader.GetString(2), FileSize = reader.GetInt64(3),
        LastWriteUtc = ParseUtc(reader.GetString(4)), Width = reader.GetInt32(5), Height = reader.GetInt32(6), PositivePrompt = GetString(reader, 7),
        NegativePrompt = GetString(reader, 8), ModelName = GetString(reader, 9), LorasJson = GetString(reader, 10), ParseStatus = ParseStatusValue(GetString(reader, 11)),
        IsFavorite = reader.GetInt64(12) == 1, Rating = GetNullableInt(reader, 13), Note = GetString(reader, 14), LocationCount = reader.GetInt32(15)
    };

    private static void AddSearchParameters(SqliteCommand command, string? text, string? model, string? lora, string? tag, string? category, bool favoritesOnly)
    {
        command.Parameters.AddWithValue("$favoritesOnly", favoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("$text", text?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$model", model?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$lora", lora?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$tag", tag?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$category", category?.Trim() ?? string.Empty);
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? GetNullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    private static string TagKey(string kind, string normalizedText) => $"{kind}\u001f{normalizedText}";
    private static void EnsureColumn(SqliteConnection connection, string table, string columnDefinition)
    {
        var columnName = columnDefinition.Split(' ', 2)[0];
        var exists = false;
        using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = $"PRAGMA table_info({table});";
            using var rows = inspect.ExecuteReader();
            while (rows.Read())
            {
                if (string.Equals(rows.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {columnDefinition};";
        alter.ExecuteNonQuery();
    }
    private static string UtcNow() => DateTime.UtcNow.ToString("O");
    private static DateTime ParseUtc(string? value) => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
    private static ParseStatus ParseStatusValue(string? value) => Enum.TryParse<ParseStatus>(value, out var status) ? status : ParseStatus.NoMetadata;
}
