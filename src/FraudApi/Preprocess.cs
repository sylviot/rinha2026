using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FraudApi;

public static class Preprocess
{
    public const string DataDir = "/refs";
    public const string RefsBin = "/refs/refs.bin";
    public const string ReadyFlag = "/refs/ready";
    public const string LockFile = "/refs/.lock";
    private const string ReferencesGz = "/input/references.json.gz";
    private const string RawPath = "/refs/raw.bin";
    public const string McccRiskPath = "/input/mcc_risk.json";
    private const string EmbeddedBin = "/app/refs.bin";

    // Inverted-file (IVF) parameters. With 3M refs:
    //   avg cluster size  = 3M / 1024  ≈ 2930
    //   probed per query  = 16 × 2930  ≈ 47K (vs 3M brute force ≈ 60x speedup)
    public const int NumCentroids = 1024;
    public const int NumProbe = 16;

    private const uint Magic = 0x52494E33;      // "RIN3"
    private const uint Version = 2;
    private const int HeaderSize = 64;
    private const int RawEntrySize = Featurize.Stride + 1; // vector + label byte

    public static async Task EnsureAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        if (File.Exists(ReadyFlag) && IsValidBinary()) return;

        // Fast path: pre-built binary shipped inside the image — copy once to the volume.
        if (File.Exists(EmbeddedBin) && !IsValidBinary())
        {
            FileStream? copyLock = null;
            try { copyLock = new FileStream(LockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException)
            {
                // Another instance is already copying; wait for it.
                while (!File.Exists(ReadyFlag)) { ct.ThrowIfCancellationRequested(); await Task.Delay(200, ct); }
                return;
            }
            try
            {
                if (IsValidBinary()) return; // won the race but other instance already finished
                Console.WriteLine("[preprocess] copying pre-built refs.bin from image...");
                var t0 = Environment.TickCount64;
                var tmp = RefsBin + ".tmp";
                File.Copy(EmbeddedBin, tmp, overwrite: true);
                File.Move(tmp, RefsBin, overwrite: true);
                File.WriteAllText(ReadyFlag, DateTime.UtcNow.ToString("o"));
                Console.WriteLine($"[preprocess] ready in {Environment.TickCount64 - t0}ms (pre-built)");
            }
            finally { copyLock.Dispose(); try { File.Delete(LockFile); } catch { } }
            return;
        }
        if (File.Exists(ReadyFlag)) File.Delete(ReadyFlag);

        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(LockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            Console.WriteLine("[preprocess] another instance is preprocessing; waiting...");
            while (!File.Exists(ReadyFlag))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct);
            }
            return;
        }

        try
        {
            Console.WriteLine("[preprocess] building refs.bin from references.json.gz");
            var t0 = Environment.TickCount64;
            BuildBinary();
            Console.WriteLine($"[preprocess] done in {Environment.TickCount64 - t0}ms");
            File.WriteAllText(ReadyFlag, DateTime.UtcNow.ToString("o"));
        }
        finally
        {
            lockStream.Dispose();
            try { File.Delete(LockFile); } catch { }
        }
    }

    private static bool IsValidBinary()
    {
        if (!File.Exists(RefsBin)) return false;
        try
        {
            using var fs = File.OpenRead(RefsBin);
            Span<byte> hdr = stackalloc byte[16];
            if (fs.Read(hdr) != 16) return false;
            return BitConverter.ToUInt32(hdr[..4]) == Magic
                && BitConverter.ToUInt32(hdr[4..8]) == Version;
        }
        catch { return false; }
    }

    private static void BuildBinary()
    {
        if (!File.Exists(ReferencesGz))
            throw new FileNotFoundException("missing references.json.gz", ReferencesGz);

        if (File.Exists(RawPath)) File.Delete(RawPath);

        // Stage 1: stream-parse the gz into a raw flat file, sampling centroids on the fly.
        Console.WriteLine("[preprocess] stage 1/3: parsing + sampling centroids");
        var centroids = new sbyte[NumCentroids * Featurize.Stride];
        int count = StreamParse(RawPath, centroids);
        Console.WriteLine($"[preprocess]   parsed {count:N0} vectors");

        // Stage 2: assign each vector to its nearest centroid (mmap the raw file).
        Console.WriteLine("[preprocess] stage 2/3: assigning clusters");
        var clusterIds = new ushort[count];
        var sizes = new int[NumCentroids];
        AssignClusters(count, centroids, clusterIds, sizes);

        var offsets = new int[NumCentroids + 1];
        for (int i = 0; i < NumCentroids; i++) offsets[i + 1] = offsets[i] + sizes[i];

        // Stage 3: scatter raw vectors+labels into cluster-sorted order, write final file.
        Console.WriteLine("[preprocess] stage 3/3: writing final refs.bin");
        WriteFinal(count, centroids, clusterIds, offsets);

        File.Delete(RawPath);
    }

    private static int StreamParse(string rawOutPath, sbyte[] centroids)
    {
        using var input = new FileStream(ReferencesGz, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var rawFs = new FileStream(rawOutPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);

        var vecBuf = new sbyte[Featurize.Stride];
        var floatBuf = new float[Featurize.Dims];
        var entry = new byte[RawEntrySize];

        var buffer = new byte[1 << 16];
        int bytesInBuffer = 0;
        bool isFinalBlock = false;
        var state = new JsonReaderState();

        bool readingVector = false, readingLabel = false;
        int dimIdx = -1;
        byte currentLabel = 0;
        int count = 0;

        var rng = new Random(12345);

        while (true)
        {
            if (!isFinalBlock)
            {
                int space = buffer.Length - bytesInBuffer;
                if (space < 1024)
                {
                    var bigger = new byte[buffer.Length * 2];
                    Buffer.BlockCopy(buffer, 0, bigger, 0, bytesInBuffer);
                    buffer = bigger;
                    space = buffer.Length - bytesInBuffer;
                }
                int read = gz.Read(buffer, bytesInBuffer, space);
                if (read == 0) isFinalBlock = true;
                else bytesInBuffer += read;
            }

            var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesInBuffer), isFinalBlock, state);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartArray:
                        if (readingVector) dimIdx = 0;
                        break;
                    case JsonTokenType.EndArray:
                        if (readingVector) { readingVector = false; dimIdx = -1; }
                        break;
                    case JsonTokenType.EndObject:
                        Featurize.Quantize(floatBuf, vecBuf);
                        MemoryMarshal.AsBytes(vecBuf.AsSpan()).CopyTo(entry.AsSpan(0, Featurize.Stride));
                        entry[Featurize.Stride] = currentLabel;
                        rawFs.Write(entry);

                        // Reservoir sampling for centroids.
                        if (count < NumCentroids)
                        {
                            Buffer.BlockCopy(entry, 0, centroids, count * Featurize.Stride, Featurize.Stride);
                        }
                        else
                        {
                            int j = rng.Next(count + 1);
                            if (j < NumCentroids)
                                Buffer.BlockCopy(entry, 0, centroids, j * Featurize.Stride, Featurize.Stride);
                        }

                        count++;
                        if ((count & 0xFFFFF) == 0)
                            Console.WriteLine($"[preprocess]   {count:N0} vectors");
                        break;
                    case JsonTokenType.PropertyName:
                        if (reader.ValueTextEquals("vector"u8)) readingVector = true;
                        else if (reader.ValueTextEquals("label"u8)) readingLabel = true;
                        break;
                    case JsonTokenType.Number:
                        if (readingVector && dimIdx >= 0 && dimIdx < Featurize.Dims)
                            floatBuf[dimIdx++] = (float)reader.GetDouble();
                        break;
                    case JsonTokenType.String:
                        if (readingLabel)
                        {
                            currentLabel = reader.ValueTextEquals("fraud"u8) ? (byte)1 : (byte)0;
                            readingLabel = false;
                        }
                        break;
                }
            }

            state = reader.CurrentState;
            int consumed = (int)reader.BytesConsumed;
            if (consumed < bytesInBuffer)
            {
                int remaining = bytesInBuffer - consumed;
                Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                bytesInBuffer = remaining;
            }
            else bytesInBuffer = 0;

            if (isFinalBlock && bytesInBuffer == 0) break;
        }

        rawFs.Flush(true);
        return count;
    }

    private static unsafe void AssignClusters(int count, sbyte[] centroids, ushort[] clusterIds, int[] sizes)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(RawPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            fixed (sbyte* centPtr = centroids)
            {
                for (int i = 0; i < count; i++)
                {
                    sbyte* vec = (sbyte*)(p + (long)i * RawEntrySize);
                    int bestC = 0;
                    int bestD = int.MaxValue;
                    for (int c = 0; c < NumCentroids; c++)
                    {
                        int d = Dataset.DistanceL2(vec, centPtr + c * Featurize.Stride);
                        if (d < bestD) { bestD = d; bestC = c; }
                    }
                    clusterIds[i] = (ushort)bestC;
                    sizes[bestC]++;
                    if ((i & 0xFFFFF) == 0 && i > 0)
                        Console.WriteLine($"[preprocess]   assigned {i:N0}/{count:N0}");
                }
            }
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    private static unsafe void WriteFinal(int count, sbyte[] centroids, ushort[] clusterIds, int[] offsets)
    {
        // Build the permutation indices[j] = raw position of the j-th cluster-sorted vector.
        // 12MB int[] is much cheaper than allocating a 48MB sortedVecs buffer.
        var indices = new int[count];
        var cursor = new int[NumCentroids];
        Array.Copy(offsets, cursor, NumCentroids);
        for (int i = 0; i < count; i++)
            indices[cursor[clusterIds[i]]++] = i;

        var tmp = RefsBin + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        using (var mmf = MemoryMappedFile.CreateFromFile(RawPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
        {
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                BitConverter.TryWriteBytes(header[0..4], Magic);
                BitConverter.TryWriteBytes(header[4..8], Version);
                BitConverter.TryWriteBytes(header[8..12], count);
                BitConverter.TryWriteBytes(header[12..16], Featurize.Dims);
                BitConverter.TryWriteBytes(header[16..20], NumCentroids);
                BitConverter.TryWriteBytes(header[20..24], Featurize.Stride);
                fs.Write(header);
                fs.Write(MemoryMarshal.AsBytes(centroids.AsSpan()));
                fs.Write(MemoryMarshal.AsBytes(offsets.AsSpan()));

                // Stream vectors in cluster-sorted order (16 B per record).
                var writeBuf = new byte[64 * 1024];
                int bufPos = 0;
                for (int j = 0; j < count; j++)
                {
                    byte* src = p + (long)indices[j] * RawEntrySize;
                    Buffer.MemoryCopy(src, Unsafe.AsPointer(ref writeBuf[bufPos]), Featurize.Stride, Featurize.Stride);
                    bufPos += Featurize.Stride;
                    if (bufPos == writeBuf.Length) { fs.Write(writeBuf, 0, bufPos); bufPos = 0; }
                }
                if (bufPos > 0) fs.Write(writeBuf, 0, bufPos);

                // Stream labels in cluster-sorted order (1 B per record).
                bufPos = 0;
                for (int j = 0; j < count; j++)
                {
                    writeBuf[bufPos++] = p[(long)indices[j] * RawEntrySize + Featurize.Stride];
                    if (bufPos == writeBuf.Length) { fs.Write(writeBuf, 0, bufPos); bufPos = 0; }
                }
                if (bufPos > 0) fs.Write(writeBuf, 0, bufPos);

                fs.Flush(true);
            }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }

        if (File.Exists(RefsBin)) File.Delete(RefsBin);
        File.Move(tmp, RefsBin);
    }

    public static Dictionary<string, double> LoadMccRisk()
    {
        if (!File.Exists(McccRiskPath))
            return new Dictionary<string, double>();
        using var fs = File.OpenRead(McccRiskPath);
        var d = JsonSerializer.Deserialize(fs, AppJsonContext.Default.DictionaryStringDouble);
        return d ?? new Dictionary<string, double>();
    }
}
