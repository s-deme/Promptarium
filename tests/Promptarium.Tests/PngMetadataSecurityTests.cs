using System.IO.Compression;
using System.Text;
using Promptarium.Services;
using Xunit;

namespace Promptarium.Tests;

public sealed class PngMetadataSecurityTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"png-security-{Guid.NewGuid():N}.png");
    private const int Limit = 8 * 1024 * 1024;

    [Fact]
    public void SkipsLargeImageDataAndReadsBoundedText()
    {
        Write(("IDAT", new byte[Limit + 1]), ("tEXt", Encoding.Latin1.GetBytes("key\0value")));
        Assert.Equal("value", new PngMetadataReader().Read(path).Text["key"]);
    }

    [Fact]
    public void AcceptsInputAtLimitAndRejectsAboveLimit()
    {
        var data = new byte[Limit]; data[0] = (byte)'k'; data[1] = 0;
        Write(("tEXt", data));
        Assert.Equal(Limit - 2, new PngMetadataReader().Read(path).Text["k"].Length);
        Write(("tEXt", new byte[Limit + 1]));
        Assert.Throws<InvalidDataException>(() => new PngMetadataReader().Read(path));
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public void RejectsSmallCompressedChunksWithOversizedOutput(string type)
    {
        using var compressed = new MemoryStream();
        compressed.Write(type == "zTXt" ? [(byte)'k', 0, 0] : [(byte)'k', 0, 1, 0, 0, 0]);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(new byte[Limit + 1]);
        Write((type, compressed.ToArray()));
        Assert.Throws<InvalidDataException>(() => new PngMetadataReader().Read(path));
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public void AcceptsCompressedOutputAtLimit(string type)
    {
        using var compressed = new MemoryStream();
        compressed.Write(type == "zTXt" ? [(byte)'k', 0, 0] : [(byte)'k', 0, 1, 0, 0, 0]);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(new byte[Limit]);
        Write((type, compressed.ToArray()));
        Assert.Equal(Limit, new PngMetadataReader().Read(path).Text["k"].Length);
    }

    [Fact]
    public void RejectsCumulativeLimitAndCancellation()
    {
        var data = new byte[Limit]; data[0] = (byte)'k';
        Write(("tEXt", data), ("tEXt", data), ("tEXt", data), ("tEXt", data));
        Assert.NotEmpty(new PngMetadataReader().Read(path).Text);
        Write(("tEXt", data), ("tEXt", data), ("tEXt", data), ("tEXt", data), ("tEXt", data));
        Assert.Throws<InvalidDataException>(() => new PngMetadataReader().Read(path));
        Assert.Throws<OperationCanceledException>(() => new PngMetadataReader().Read(path, new CancellationToken(true)));
    }

    [Fact]
    public async Task CancelsAnActiveReadAndReleasesTheFile()
    {
        using var compressed = new MemoryStream();
        compressed.Write([(byte)'k', 0, 0]);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(new byte[Limit]);
        var data = compressed.ToArray();
        Write(("zTXt", data), ("zTXt", data), ("zTXt", data));
        using var cancellation = new CancellationTokenSource();
        var reading = Task.Run(() => new PngMetadataReader().Read(path, cancellation.Token));
        var observedOpenRead = false;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (reading.IsCompleted) return true;
            try
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                observedOpenRead = true;
                cancellation.Cancel();
                return true;
            }
        }, TimeSpan.FromSeconds(5)));
        Assert.True(observedOpenRead, "The reader must have opened the file before cancellation.");
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reading);
        using var reopened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [Fact]
    public void RejectsTruncatedChunkLength()
    {
        Write(("tEXt", Encoding.ASCII.GetBytes("k\0v")));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(13);
        Assert.Throws<InvalidDataException>(() => new PngMetadataReader().Read(path));
    }

    private void Write(params (string type, byte[] data)[] chunks)
    {
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        foreach (var (type, data) in chunks) Chunk(file, type, data);
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = (uint)data.Length;
        stream.Write([(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
        stream.Write(Encoding.ASCII.GetBytes(type)); stream.Write(data);
        stream.Write(new byte[4]); // Metadata reader delegates CRC validation to the image decoder.
    }

    public void Dispose() => File.Delete(path);
}
