using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;

namespace OpenSmc.Documentation;


public static class Helper
{
    private static readonly Guid EmbeddedSource = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    private static readonly Encoding DefaultEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static string GetEmbeddedSource(this MetadataReader reader, DocumentHandle document)
    {
        byte[] bytes = (from handle in reader.GetCustomDebugInformation(document)
            let cdi = reader.GetCustomDebugInformation(handle)
            where reader.GetGuid(cdi.Kind) == EmbeddedSource
            select reader.GetBlobBytes(cdi.Value)).SingleOrDefault();

        if (bytes == null)
        {
            return null;
        }

        int uncompressedSize = BitConverter.ToInt32(bytes, 0);
        var stream = new MemoryStream(bytes, sizeof(int), bytes.Length - sizeof(int));

        if (uncompressedSize != 0)
        {
            var decompressed = new MemoryStream(uncompressedSize);

            using (var deflater = new DeflateStream(stream, CompressionMode.Decompress))
            {
                deflater.CopyTo(decompressed);
            }

            if (decompressed.Length != uncompressedSize)
            {
                throw new InvalidDataException();
            }

            stream = decompressed;
        }

        using (stream)
        {
            return Decode(stream, DefaultEncoding);
        }
    }

    private static string Decode(MemoryStream stream, Encoding encoding)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        encoding = encoding ?? DefaultEncoding;
        stream.Position = 0;

        using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
        {
            var text = reader.ReadToEnd();
            return text;
        }
    }
}
