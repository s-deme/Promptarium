using System.Security.Cryptography;
using System.IO;
using Promptarium.Models;

namespace Promptarium.Services;

public sealed class ImageScanner
{
    private readonly LibraryDatabase _database;
    private readonly PngMetadataReader _metadataReader;
    private readonly ComfyWorkflowParser _workflowParser;
    private readonly PromptClassifier _classifier;

    public ImageScanner(LibraryDatabase database, PngMetadataReader metadataReader, ComfyWorkflowParser workflowParser, PromptClassifier classifier)
    {
        _database = database;
        _metadataReader = metadataReader;
        _workflowParser = workflowParser;
        _classifier = classifier;
    }

    public async Task<ScanResult> ScanAllAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var roots = _database.GetScanRoots();
        var started = DateTime.UtcNow;
        var totalDiscovered = 0;
        var totalRegistered = 0;
        var totalFailed = 0;

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootStart = DateTime.UtcNow;
            if (!Directory.Exists(root.Path))
            {
                _database.MarkMissingLocations(root.Id, rootStart);
                continue;
            }

            var files = EnumeratePngFiles(root).ToList();
            totalDiscovered += files.Count;
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = files[index];
                progress?.Report(new ScanProgress
                {
                    Discovered = totalDiscovered,
                    Processed = index,
                    Registered = totalRegistered,
                    Failed = totalFailed,
                    CurrentPath = path
                });

                try
                {
                    var file = new FileInfo(path);
                    PngMetadata metadata;
                    ParsedGeneration generation;
                    try
                    {
                        metadata = await Task.Run(() => _metadataReader.Read(path), cancellationToken).ConfigureAwait(false);
                        generation = _workflowParser.Parse(metadata);
                    }
                    catch (Exception exception)
                    {
                        metadata = new PngMetadata { IsPng = true };
                        generation = new ParsedGeneration
                        {
                            Status = ParseStatus.Failed,
                            Message = $"PNGメタデータを読み取れません: {exception.Message}"
                        };
                    }
                    var tags = _classifier.Classify(generation.PositivePrompt, "positive");
                    tags.AddRange(_classifier.Classify(generation.NegativePrompt, "negative"));
                    var hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
                    _database.UpsertImage(root.Id, path, file, hash, metadata, generation, tags, rootStart);
                    totalRegistered++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    totalFailed++;
                }
            }

            _database.MarkMissingLocations(root.Id, rootStart);
        }

        progress?.Report(new ScanProgress
        {
            Discovered = totalDiscovered,
            Processed = totalDiscovered,
            Registered = totalRegistered,
            Failed = totalFailed
        });
        return new ScanResult(totalDiscovered, totalRegistered, totalFailed, DateTime.UtcNow - started);
    }

    private static IEnumerable<string> EnumeratePngFiles(ScanRoot root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = root.IncludeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root.Path, "*.png", options);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 131072, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
