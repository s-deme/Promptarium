using Promptarium.Models;
using Promptarium.Services;
using Xunit;

namespace Promptarium.Tests;

public sealed class LibraryAndScannerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"Promptarium-xUnit-{Guid.NewGuid():N}");

    [Fact]
    public void Database_keeps_sources_edits_filters_suggestions_and_history_separate()
    {
        var imagePath = Path.Combine(_testRoot, "images", "sample.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [1, 2, 3]);
        var database = CreateDatabase();
        var rootId = database.UpsertScanRoot(Path.GetDirectoryName(imagePath)!);
        var generation = new ParsedGeneration
        {
            Status = ParseStatus.Parsed,
            PositivePrompt = "hero standing",
            ModelName = "model.safetensors",
            Seed = "123",
            WorkflowWidth = "640",
            WorkflowHeight = "960",
            ValueSource = "ComfyUI prompt JSON"
        };
        generation.SetSource("positive_prompt", "ComfyUI prompt JSON");
        generation.SetSource("model_name", "ComfyUI prompt JSON");
        generation.SetSource("seed", "ComfyUI prompt JSON");
        generation.SetSource("workflow_width", "ComfyUI prompt JSON");
        generation.SetSource("workflow_height", "ComfyUI prompt JSON");
        var tags = new[] { new PromptTag { PromptKind = "positive", Ordinal = 0, RawText = "standing", NormalizedText = "standing", Category = "ポーズ／行動" } };
        var assetId = database.UpsertImage(rootId, imagePath, new FileInfo(imagePath), new string('a', 64), new PngMetadata { IsPng = true, Width = 640, Height = 960 }, generation, tags, DateTime.UtcNow);
        var detail = Assert.IsType<ImageDetail>(database.GetDetail(assetId));

        Assert.Equal("640", detail.WorkflowWidth);
        Assert.Contains("positive_prompt", detail.ValueSourcesJson);
        database.SaveUserEdits(assetId, new UserEdits { PositivePrompt = "edited hero", IsFavorite = true, Rating = 4, Note = "keep" }, new UserGenerationOverrides { Width = "768" }, detail.Tags);
        detail = Assert.IsType<ImageDetail>(database.GetDetail(assetId));
        Assert.Equal("edited hero", detail.ManualPositivePrompt);
        Assert.Equal("768", detail.ManualWidth);
        Assert.Equal("hero standing", detail.PositivePrompt);

        var match = new LibrarySearch { Text = "edited", ParseStatus = ParseStatus.Parsed, MinimumRating = 4, MinimumWidth = 640, MinimumHeight = 960 };
        Assert.Equal(1, database.CountSearchResults(match));
        Assert.Equal(0, database.CountSearchResults(new LibrarySearch { MinimumWidth = 641 }));
        Assert.Contains("standing", database.GetTagSuggestions("stand"));

        database.SaveRecentSearch("edited hero");
        Assert.Equal("edited hero", database.GetRecentSearches().First());
        database.SetScanRootEnabled(rootId, false);
        Assert.Empty(database.GetScanRoots());
        Assert.Single(database.GetScanRoots(includeDisabled: true));
        database.DeleteScanRoot(rootId);
        Assert.NotNull(database.GetDetail(assetId));
    }

    [Fact]
    public async Task Scanner_skips_unchanged_files_and_force_rescan_processes_them_again()
    {
        var directory = Path.Combine(_testRoot, "scan");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "minimal.png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlK4V8AAAAASUVORK5CYII="));
        var database = CreateDatabase();
        database.UpsertScanRoot(directory);
        var scanner = new ImageScanner(database, new PngMetadataReader(), new ComfyWorkflowParser(), new PromptClassifier());

        var first = await scanner.ScanAllAsync();
        var second = await scanner.ScanAllAsync();
        var forced = await scanner.ScanAllAsync(forceRescan: true);

        Assert.Equal(1, first.Registered);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(0, second.Registered);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, forced.Registered);
        Assert.Equal(0, forced.Skipped);
    }

    [Fact]
    public async Task Scanner_records_limit_reason_and_continues_with_other_images()
    {
        var directory = Path.Combine(_testRoot, "bounded-scan");
        Directory.CreateDirectory(directory);
        var invalid = Path.Combine(directory, "oversized.png");
        using (var stream = File.Create(invalid))
        {
            stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            // A text chunk one byte over 8 MiB, followed by its CRC and IEND.
            stream.Write([0, 128, 0, 1, (byte)'t', (byte)'E', (byte)'X', (byte)'t']);
            stream.Write(new byte[8 * 1024 * 1024 + 1]);
            stream.Write(new byte[4]);
            stream.Write([0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        }
        File.WriteAllBytes(Path.Combine(directory, "normal.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlK4V8AAAAASUVORK5CYII="));
        var database = CreateDatabase(); database.UpsertScanRoot(directory);
        var scanner = new ImageScanner(database, new PngMetadataReader(), new ComfyWorkflowParser(), new PromptClassifier());
        var result = await scanner.ScanAllAsync();
        Assert.Equal(2, result.Registered);
        var failures = database.Search(new LibrarySearch { ParseStatus = ParseStatus.Failed }, 10, 0);
        var failed = Assert.Single(failures);
        Assert.Contains("exceeds the limit", database.GetDetail(failed.Id)!.ParseMessage);
        Assert.Equal(1, database.CountSearchResults(new LibrarySearch { ParseStatus = ParseStatus.NoMetadata }));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_testRoot)) return;
        try { Directory.Delete(_testRoot, recursive: true); }
        catch (IOException) { }
    }

    private LibraryDatabase CreateDatabase()
    {
        var database = new LibraryDatabase(Path.Combine(_testRoot, "catalog", "promptarium.db"));
        database.Initialize();
        return database;
    }
}
