using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Promptarium.Models;
using Promptarium.Services;

var testRoot = Path.Combine(Path.GetTempPath(), $"Promptarium-Smoke-{Guid.NewGuid():N}");

try
{
    await RunAsync(testRoot);
    Console.WriteLine("Promptarium smoke tests passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Promptarium smoke tests failed: {exception}");
    return 1;
}
finally
{
    if (Directory.Exists(testRoot))
    {
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Temporary smoke-test files could not be removed: {exception.Message}");
        }
    }
}

static async Task RunAsync(string testRoot)
{
    VerifyLegacyMigration(Path.Combine(testRoot, "legacy", "promptarium.db"));

    var rootA = Path.Combine(testRoot, "root-a");
    var rootB = Path.Combine(testRoot, "root-b");
    var nested = Path.Combine(rootA, "nested");
    Directory.CreateDirectory(nested);
    Directory.CreateDirectory(rootB);

    var standardPrompt = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "comfy-standard.json"));
    var workflow = "{\"last_node_id\":9,\"nodes\":[]}";
    var standardPath = Path.Combine(nested, "comfy-standard.png");
    PngFixture.Write(standardPath, 4, 3, new Dictionary<string, string>
    {
        ["prompt"] = standardPrompt,
        ["workflow"] = workflow,
        ["note"] = "iTXt metadata is retained"
    });
    File.Copy(standardPath, Path.Combine(rootB, "duplicate.png"));

    PngFixture.Write(Path.Combine(rootA, "no-metadata.png"), 2, 2, new Dictionary<string, string>());
    var outsidePath = Path.Combine(testRoot, "outside-registered-roots.png");
    PngFixture.Write(outsidePath, 1, 1, new Dictionary<string, string>());
    PngFixture.Write(Path.Combine(rootA, "corrupt-prompt.png"), 2, 3, new Dictionary<string, string>
    {
        ["prompt"] = "{ invalid JSON",
        ["workflow"] = workflow
    });
    PngFixture.Write(Path.Combine(rootA, "unknown-node.png"), 3, 2, new Dictionary<string, string>
    {
        ["prompt"] = """
            {
              "1":{"class_type":"CLIPTextEncode","inputs":{"text":"unknown node test"}},
              "2":{"class_type":"KSampler","inputs":{"seed":42,"positive":["1",0],"latent_image":["3",0]}},
              "3":{"class_type":"MyCustomNode","inputs":{}},
              "4":{"class_type":"SaveImage","inputs":{"images":["2",0]}}
            }
            """
    });

    var reader = new PngMetadataReader();
    var extracted = reader.Read(standardPath);
    Assert(extracted.IsPng && extracted.Width == 4 && extracted.Height == 3, "PNG IHDR dimensions were not read.");
    Assert(extracted.Text.ContainsKey("prompt") && extracted.Text.ContainsKey("workflow") && extracted.Text["note"] == "iTXt metadata is retained", "PNG text chunks were not retained.");

    var parser = new ComfyWorkflowParser();
    var parsed = parser.Parse(extracted);
    Assert(parsed.Status == ParseStatus.Parsed, "Standard ComfyUI fixture should parse cleanly.");
    Assert(parsed.PositivePrompt == "1girl, standing, beach, masterpiece, wide shot", "Positive prompt was not resolved through KSampler links.");
    Assert(parsed.NegativePrompt == "low quality, blurry", "Negative prompt was not resolved through KSampler links.");
    Assert(parsed.ModelName == "demo-model.safetensors" && parsed.VaeName == "demo-vae.safetensors", "Model or VAE was not parsed.");
    Assert(parsed.Seed == "123456" && parsed.Steps == "28" && parsed.Cfg == "7.5" && parsed.Sampler == "euler" && parsed.Scheduler == "normal", "Sampler parameters were not parsed.");
    Assert(parsed.Loras.Single().Name == "anime-lora.safetensors", "LoRA was not parsed.");

    var workflowOnly = new PngMetadata { IsPng = true };
    workflowOnly.Text["workflow"] = """
        {
          "nodes": [
            {"id": 1, "type": "CLIPTextEncode", "widgets_values": ["workflow positive"]},
            {"id": 2, "type": "CLIPTextEncode", "widgets_values": ["workflow negative"]},
            {"id": 3, "type": "KSampler", "widgets_values": [321, "fixed", 24, 6.5, "euler", "normal"], "inputs": [{"name":"positive", "link": 10}, {"name":"negative", "link": 11}, {"name":"model", "link": 12}]},
            {"id": 4, "type": "CheckpointLoaderSimple", "widgets_values": ["workflow-model.safetensors"]},
            {"id": 5, "type": "VAEDecode", "inputs": [{"name":"samples", "link": 13}]},
            {"id": 6, "type": "SaveImage", "inputs": [{"name":"images", "link": 14}]}
          ],
          "links": [[10, 1, 0, 3, 0, "CONDITIONING"], [11, 2, 0, 3, 1, "CONDITIONING"], [12, 4, 0, 3, 2, "MODEL"], [13, 3, 0, 5, 0, "LATENT"], [14, 5, 0, 6, 0, "IMAGE"]]
        }
        """;
    var workflowParsed = parser.Parse(workflowOnly);
    Assert(workflowParsed.Status == ParseStatus.Partial && workflowParsed.PositivePrompt == "workflow positive" && workflowParsed.NegativePrompt == "workflow negative" && workflowParsed.ModelName == "workflow-model.safetensors" && workflowParsed.ValueSource?.StartsWith("ComfyUI workflow JSON", StringComparison.Ordinal) == true, "Workflow-only metadata was not derived separately from API prompt JSON.");

    var classifier = new PromptClassifier();
    var categories = classifier.Classify(parsed.PositivePrompt).ToDictionary(tag => tag.RawText, tag => tag.Category);
    Assert(categories["1girl"] == "キャラクター" && categories["standing"] == "ポーズ／行動" && categories["beach"] == "シチュエーション／環境" && categories["masterpiece"] == "スタイル／品質" && categories["wide shot"] == "構図／カメラ", "Prompt categories were not assigned as expected.");

    var database = new LibraryDatabase(Path.Combine(testRoot, "catalog", "promptarium.db"));
    database.Initialize();
    database.UpsertScanRoot(rootA);
    database.UpsertScanRoot(rootB);
    Assert(database.GetScanRoots().Count == 2, "Multiple scan roots were not saved.");

    var scanner = new ImageScanner(database, reader, parser, classifier);
    var scan = await scanner.ScanAllAsync();
    Assert(scan.Discovered == 5 && scan.Registered == 5 && scan.Failed == 0, "Recursive PNG scan did not register every fixture.");
    Assert(database.CountSearchResults(null, null, null, null, null, false) == 4, "Duplicate files were not merged into one image asset.");
    Assert(database.Search(null, null, null, null, null, false, 10, 0).All(summary => !string.Equals(summary.PrimaryPath, outsidePath, StringComparison.OrdinalIgnoreCase)), "Files outside registered scan roots must not be indexed.");
    Assert(database.CountSearchResults("1girl", "demo-model", "anime-lora", "standing", "ポーズ／行動", false) == 1, "AND search across text, model, LoRA, tag and category failed.");

    var standard = database.Search("1girl", null, null, null, null, false, 10, 0).Single();
    Assert(standard.LocationCount == 2 && standard.ParseStatus == ParseStatus.Parsed, "Duplicate locations or parse status were not persisted.");
    Assert(database.Search("unknown node test", null, null, null, null, false, 10, 0).Single().ParseStatus == ParseStatus.UnknownNodes, "Unknown nodes were not represented as a distinct status.");
    Assert(database.Search(null, null, null, null, null, false, 10, 0).Single(summary => Path.GetFileName(summary.PrimaryPath) == "corrupt-prompt.png").ParseStatus == ParseStatus.Corrupt, "Corrupt metadata was not persisted as a distinct status.");
    Assert(database.Search(null, null, null, null, null, false, 10, 0).Single(summary => Path.GetFileName(summary.PrimaryPath) == "no-metadata.png").ParseStatus == ParseStatus.NoMetadata, "Metadata-free PNG was not registered with its own status.");

    var detail = database.GetDetail(standard.Id) ?? throw new InvalidOperationException("Standard image detail is missing.");
    Assert(detail.PromptJson == standardPrompt && detail.WorkflowJson == workflow && detail.RawMetadata?.Contains("iTXt metadata is retained", StringComparison.Ordinal) == true, "Raw prompt/workflow metadata was not preserved.");
    var standing = detail.Tags.Single(tag => tag.RawText == "standing");
    standing.Category = "構図／カメラ";
    database.SaveUserEdits(standard.Id, new UserEdits
    {
        PositivePrompt = "manual positive prompt",
        ModelName = "manual-model.safetensors",
        IsFavorite = true,
        Rating = 5,
        Note = "restorable note"
    }, new UserGenerationOverrides { Seed = "9001", Width = "1024", Height = "1536" }, detail.Tags);

    detail = database.GetDetail(standard.Id) ?? throw new InvalidOperationException("Edited image detail is missing.");
    Assert(detail.ManualPositivePrompt == "manual positive prompt" && detail.ManualSeed == "9001" && detail.ManualWidth == "1024" && detail.IsFavorite && detail.Rating == 5, "User edits were not stored separately from extracted data.");
    Assert(database.GetModels().Contains("manual-model.safetensors"), "Manual model entries are not available to the model filter.");

    var differentialScan = await scanner.ScanAllAsync();
    Assert(differentialScan.Registered == 0 && differentialScan.Skipped == 5 && differentialScan.Failed == 0, "Unchanged files should be skipped without reparsing or rehashing.");
    detail = database.GetDetail(standard.Id) ?? throw new InvalidOperationException("Rescanned image detail is missing.");
    var rescannedStanding = detail.Tags.Single(tag => tag.RawText == "standing");
    Assert(detail.ManualPositivePrompt == "manual positive prompt" && rescannedStanding.Category == "構図／カメラ" && rescannedStanding.Source == "manual" && detail.Tags.Count(tag => tag.RawText == "standing") == 1, "Rescan overwrote or duplicated a manual tag edit.");

    var movedPath = Path.Combine(rootA, "moved-standard.png");
    File.Move(standardPath, movedPath);
    await scanner.ScanAllAsync();
    detail = database.GetDetail(standard.Id) ?? throw new InvalidOperationException("Moved image detail is missing.");
    Assert(detail.LocationCount == 2 && detail.Locations.Contains(movedPath), "Renamed or moved files were not re-associated through their content hash.");

    Directory.Move(rootB, Path.Combine(testRoot, "root-b-offline"));
    await scanner.ScanAllAsync();
    detail = database.GetDetail(standard.Id) ?? throw new InvalidOperationException("Offline-root image detail is missing.");
    Assert(detail.LocationCount == 1 && detail.Locations.Any(location => location.Contains("duplicate.png（所在不明）", StringComparison.Ordinal)), "Unavailable scan roots must keep locations as missing instead of deleting them.");

    var backup = Path.Combine(testRoot, "backup", "user-data.db");
    database.BackupTo(backup);
    database.SaveUserEdits(standard.Id, new UserEdits { Note = "changed after backup" }, new UserGenerationOverrides(), detail.Tags);
    database.RestoreFrom(backup);
    detail = database.GetDetail(standard.Id) ?? throw new InvalidOperationException("Restored image detail is missing.");
    Assert(detail.Note == "restorable note" && detail.ManualSeed == "9001" && detail.Tags.Single(tag => tag.RawText == "standing").Category == "構図／カメラ", "Backup restore did not recover Promptarium-specific user data.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void VerifyLegacyMigration(string databasePath)
{
    var database = new LibraryDatabase(databasePath);
    database.Initialize();
    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
    {
        connection.Open();
        using var removeColumn = connection.CreateCommand();
        removeColumn.CommandText = "ALTER TABLE generation_records DROP COLUMN value_source;";
        removeColumn.ExecuteNonQuery();
    }

    database.Initialize();
    using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
    verify.Open();
    using var inspect = verify.CreateCommand();
    inspect.CommandText = "SELECT value_source FROM generation_records LIMIT 0;";
    inspect.ExecuteNonQuery();
}

internal static class PngFixture
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(string path, int width, int height, IReadOnlyDictionary<string, string> text)
    {
        using var stream = File.Create(path);
        stream.Write(Signature);
        WriteChunk(stream, "IHDR", CreateHeader(width, height));
        foreach (var (key, value) in text)
        {
            WriteTextChunk(stream, key, value);
        }
        WriteChunk(stream, "IDAT", Compress(CreatePixels(width, height)));
        WriteChunk(stream, "IEND", []);
    }

    private static byte[] CreateHeader(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        return header;
    }

    private static byte[] CreatePixels(int width, int height)
    {
        var pixels = new byte[height * (1 + width * 3)];
        for (var row = 0; row < height; row++)
        {
            pixels[row * (1 + width * 3)] = 0;
        }
        return pixels;
    }

    private static void WriteTextChunk(Stream stream, string key, string value)
    {
        if (key == "workflow")
        {
            var compressed = Compress(Encoding.Latin1.GetBytes(value));
            var bytes = Encoding.Latin1.GetBytes(key).Concat([(byte)0, (byte)0]).Concat(compressed).ToArray();
            WriteChunk(stream, "zTXt", bytes);
            return;
        }

        if (key == "note")
        {
            var bytes = Encoding.Latin1.GetBytes(key).Concat([(byte)0, (byte)0, (byte)0, (byte)0, (byte)0]).Concat(Encoding.UTF8.GetBytes(value)).ToArray();
            WriteChunk(stream, "iTXt", bytes);
            return;
        }

        WriteChunk(stream, "tEXt", Encoding.Latin1.GetBytes(key + "\0" + value));
    }

    private static byte[] Compress(byte[] value)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(value);
        }
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc(typeBytes, data));
        stream.Write(crc);
    }

    private static uint ComputeCrc(byte[] type, byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xffffffffu;
    }
}
