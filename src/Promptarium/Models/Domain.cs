namespace Promptarium.Models;

public enum ParseStatus
{
    Parsed,
    Partial,
    UnknownNodes,
    NoMetadata,
    Corrupt,
    Failed
}

public sealed record LoraUsage(string Name, string? ModelStrength, string? ClipStrength, int Order);

public sealed class ParsedGeneration
{
    public ParseStatus Status { get; set; } = ParseStatus.NoMetadata;
    public string? Message { get; set; }
    public string? ValueSource { get; set; }
    public string? PositivePrompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? ModelName { get; set; }
    public string? VaeName { get; set; }
    public string? Seed { get; set; }
    public string? Steps { get; set; }
    public string? Cfg { get; set; }
    public string? Sampler { get; set; }
    public string? Scheduler { get; set; }
    public string? WorkflowWidth { get; set; }
    public string? WorkflowHeight { get; set; }
    public Dictionary<string, string> ValueSources { get; } = new(StringComparer.Ordinal);
    public List<LoraUsage> Loras { get; } = [];

    public void SetSource(string field, string source)
    {
        if (!string.IsNullOrWhiteSpace(source)) ValueSources[field] = source;
    }
}

public sealed class PngMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public Dictionary<string, string> Text { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsPng { get; init; }
}

public sealed class ScanRoot
{
    public long Id { get; init; }
    public string Path { get; init; } = string.Empty;
    public bool IncludeSubfolders { get; init; }
    public bool IsEnabled { get; init; }
    public DateTime? LastScannedUtc { get; init; }
    public string DisplayName => $"{(IsEnabled ? "●" : "○")} {Path}";
}

public sealed record FileScanState(long AssetId, string ContentHash, long FileSize, DateTime LastWriteUtc);

public sealed class LibrarySearch
{
    public string Text { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Lora { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool FavoritesOnly { get; init; }
    public ParseStatus? ParseStatus { get; init; }
    public int MinimumRating { get; init; }
    public int MinimumWidth { get; init; }
    public int MinimumHeight { get; init; }
}

public class ImageSummary
{
    public long Id { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public string PrimaryPath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? PositivePrompt { get; init; }
    public string? NegativePrompt { get; init; }
    public string? ModelName { get; init; }
    public string? LorasJson { get; init; }
    public ParseStatus ParseStatus { get; init; }
    public bool IsFavorite { get; init; }
    public int? Rating { get; init; }
    public string? Note { get; init; }
    public int LocationCount { get; init; }
}

public sealed class ImageDetail : ImageSummary
{
    public string? RawMetadata { get; init; }
    public string? PromptJson { get; init; }
    public string? WorkflowJson { get; init; }
    public string? VaeName { get; init; }
    public string? Seed { get; init; }
    public string? Steps { get; init; }
    public string? Cfg { get; init; }
    public string? Sampler { get; init; }
    public string? Scheduler { get; init; }
    public string? ParseMessage { get; init; }
    public string? ParseSource { get; init; }
    public string? ValueSourcesJson { get; init; }
    public string? WorkflowWidth { get; init; }
    public string? WorkflowHeight { get; init; }
    public string? ManualPositivePrompt { get; init; }
    public string? ManualNegativePrompt { get; init; }
    public string? ManualModelName { get; init; }
    public string? ManualVaeName { get; init; }
    public string? ManualSeed { get; init; }
    public string? ManualSteps { get; init; }
    public string? ManualCfg { get; init; }
    public string? ManualSampler { get; init; }
    public string? ManualScheduler { get; init; }
    public string? ManualWidth { get; init; }
    public string? ManualHeight { get; init; }
    public List<string> Locations { get; } = [];
    public List<PromptTag> Tags { get; } = [];
}

public sealed class PromptTag
{
    public long Id { get; init; }
    public string PromptKind { get; init; } = "positive";
    public int Ordinal { get; init; }
    public string RawText { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
    public string Category { get; set; } = "未分類";
    public string Source { get; set; } = "automatic";
}

public sealed class UserEdits
{
    public string? PositivePrompt { get; init; }
    public string? NegativePrompt { get; init; }
    public string? ModelName { get; init; }
    public bool IsFavorite { get; init; }
    public int? Rating { get; init; }
    public string? Note { get; init; }
}

public sealed class UserGenerationOverrides
{
    public string? VaeName { get; init; }
    public string? Seed { get; init; }
    public string? Steps { get; init; }
    public string? Cfg { get; init; }
    public string? Sampler { get; init; }
    public string? Scheduler { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }

    public bool IsEmpty => new[] { VaeName, Seed, Steps, Cfg, Sampler, Scheduler, Width, Height }
        .All(string.IsNullOrWhiteSpace);
}

public sealed class ScanProgress
{
    public int Discovered { get; init; }
    public int Processed { get; init; }
    public int Registered { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public string? CurrentPath { get; init; }
}

public sealed record ScanResult(int Discovered, int Registered, int Skipped, int Failed, TimeSpan Elapsed);
