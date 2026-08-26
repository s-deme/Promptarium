using System.IO.Compression;
using System.IO;
using System.Text;
using Promptarium.Models;

namespace Promptarium.Services;

public sealed class PngMetadataReader
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public PngMetadata Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var signature = reader.ReadBytes(8);
        if (!signature.SequenceEqual(PngSignature))
        {
            return new PngMetadata { IsPng = false };
        }

        var metadata = new PngMetadata { IsPng = true };
        while (stream.Position + 12 <= stream.Length)
        {
            var length = ReadUInt32BigEndian(reader);
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (length > int.MaxValue || stream.Position + length + 4 > stream.Length)
            {
                throw new InvalidDataException("PNG chunk length is invalid.");
            }

            var data = reader.ReadBytes((int)length);
            _ = reader.ReadBytes(4); // CRC is validated by the image decoder when rendering.

            switch (type)
            {
                case "IHDR" when data.Length >= 8:
                    metadata = new PngMetadata
                    {
                        IsPng = true,
                        Width = ReadInt32BigEndian(data, 0),
                        Height = ReadInt32BigEndian(data, 4)
                    };
                    break;
                case "tEXt":
                    AddTextChunk(metadata.Text, data, Encoding.Latin1);
                    break;
                case "zTXt":
                    AddCompressedTextChunk(metadata.Text, data);
                    break;
                case "iTXt":
                    AddInternationalTextChunk(metadata.Text, data);
                    break;
                case "IEND":
                    return metadata;
            }
        }

        return metadata;
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException();
        }

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static int ReadInt32BigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static void AddTextChunk(IDictionary<string, string> destination, byte[] data, Encoding encoding)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, separator);
        var value = encoding.GetString(data, separator + 1, data.Length - separator - 1);
        destination[key] = value;
    }

    private static void AddCompressedTextChunk(IDictionary<string, string> destination, byte[] data)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0 || separator + 2 > data.Length)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, separator);
        var compressed = data[(separator + 2)..];
        destination[key] = InflateToString(compressed, Encoding.Latin1);
    }

    private static void AddInternationalTextChunk(IDictionary<string, string> destination, byte[] data)
    {
        var index = Array.IndexOf(data, (byte)0);
        if (index <= 0 || index + 3 > data.Length)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, index);
        var compressionFlag = data[index + 1];
        var cursor = index + 3;
        cursor = SkipNullTerminated(data, cursor); // language tag
        cursor = SkipNullTerminated(data, cursor); // translated keyword
        if (cursor > data.Length)
        {
            return;
        }

        var textBytes = data[cursor..];
        destination[key] = compressionFlag == 1
            ? InflateToString(textBytes, Encoding.UTF8)
            : Encoding.UTF8.GetString(textBytes);
    }

    private static int SkipNullTerminated(byte[] data, int start)
    {
        var end = Array.IndexOf(data, (byte)0, start);
        return end < 0 ? data.Length + 1 : end + 1;
    }

    private static string InflateToString(byte[] compressed, Encoding encoding)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return encoding.GetString(output.ToArray());
    }
}
