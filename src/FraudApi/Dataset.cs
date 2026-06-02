using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FraudApi;

public sealed unsafe class Dataset : IDisposable
{
    public const int Stride = Featurize.Stride;
    public const int Dims = Featurize.Dims;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly sbyte* _centroids;
    private readonly int* _offsets;
    private readonly sbyte* _vectors;
    private readonly byte* _labels;
    public int Count { get; }
    public int NumCentroids { get; }

    public Dataset(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;

        uint magic = *(uint*)(p + 0);
        if (magic != 0x52494E33) throw new InvalidDataException("bad magic (expected RIN3)");
        uint version = *(uint*)(p + 4);
        if (version != 2) throw new InvalidDataException("bad version");
        Count = *(int*)(p + 8);
        int dims = *(int*)(p + 12);
        if (dims != Dims) throw new InvalidDataException("bad dims");
        NumCentroids = *(int*)(p + 16);
        int stride = *(int*)(p + 20);
        if (stride != Stride) throw new InvalidDataException("bad stride");

        const int HeaderSize = 64;
        long cursor = HeaderSize;
        _centroids = (sbyte*)(p + cursor); cursor += (long)NumCentroids * Stride;
        _offsets = (int*)(p + cursor); cursor += (long)(NumCentroids + 1) * 4;
        _vectors = (sbyte*)(p + cursor); cursor += (long)Count * Stride;
        _labels = p + cursor;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }

    /// <summary>
    /// Touches every 4KB page (so the kernel pages mmap in) AND runs a handful of
    /// real Score() calls so JIT/AOT branch predictors and CPU caches are warm by
    /// the time real traffic arrives. Reduces p99 cold-start spikes substantially.
    /// </summary>
    public long WarmUp()
    {
        // 1) Page-in: every 4KB so future mmap reads don't take page-fault hits.
        long sum = 0;
        long total = (long)NumCentroids * Stride + (long)(NumCentroids + 1) * 4
                   + (long)Count * Stride + Count;
        byte* p = (byte*)_centroids;
        for (long off = 0; off < total; off += 4096) sum += p[off];

        // 2) CPU/branch-predictor warmup: exercise the hot scoring path with
        //    a few varied queries so the AOT code is fully resident in L1i and
        //    the inner-loop branches have predictable history.
        Span<sbyte> q = stackalloc sbyte[Stride];
        var rng = new Random(7);
        for (int iter = 0; iter < 256; iter++)
        {
            for (int d = 0; d < Dims; d++) q[d] = (sbyte)(rng.Next(-127, 128));
            sum += ScoreFrauds(q);
        }
        return sum;
    }

    public double Score(ReadOnlySpan<sbyte> query) => ScoreFrauds(query) / 5.0;

    /// <summary>Returns the number of fraud-labeled neighbors among the top-5 (0..5).</summary>
    public int ScoreFrauds(ReadOnlySpan<sbyte> query)
    {
        Span<sbyte> qbuf = stackalloc sbyte[Stride];
        query.CopyTo(qbuf);
        int nc = NumCentroids;
        int np = Preprocess.NumProbe;

        fixed (sbyte* qp = qbuf)
        {
            // 1) Distance to every centroid.
            Span<int> centDists = stackalloc int[nc];
            for (int c = 0; c < nc; c++)
                centDists[c] = DistanceL2(qp, _centroids + c * Stride);

            // 2) Top-NumProbe centroids (small selection sort, np << nc).
            Span<int> topIdx = stackalloc int[np];
            Span<int> topDist = stackalloc int[np];
            for (int i = 0; i < np; i++) { topDist[i] = int.MaxValue; topIdx[i] = 0; }
            for (int c = 0; c < nc; c++)
            {
                int d = centDists[c];
                if (d >= topDist[np - 1]) continue;
                int j = np - 1;
                while (j > 0 && d < topDist[j - 1]) { topDist[j] = topDist[j - 1]; topIdx[j] = topIdx[j - 1]; j--; }
                topDist[j] = d; topIdx[j] = c;
            }

            // 3) Brute-force KNN top-5 over the selected clusters.
            int d0 = int.MaxValue, d1 = int.MaxValue, d2 = int.MaxValue, d3 = int.MaxValue, d4 = int.MaxValue;
            int i0 = 0, i1 = 0, i2 = 0, i3 = 0, i4 = 0;

            for (int p = 0; p < np; p++)
            {
                int cluster = topIdx[p];
                int start = _offsets[cluster];
                int end = _offsets[cluster + 1];
                for (int i = start; i < end; i++)
                {
                    int dist = DistanceL2(qp, _vectors + (long)i * Stride);
                    if (dist >= d4) continue;
                    if (dist < d0) { d4 = d3; i4 = i3; d3 = d2; i3 = i2; d2 = d1; i2 = i1; d1 = d0; i1 = i0; d0 = dist; i0 = i; }
                    else if (dist < d1) { d4 = d3; i4 = i3; d3 = d2; i3 = i2; d2 = d1; i2 = i1; d1 = dist; i1 = i; }
                    else if (dist < d2) { d4 = d3; i4 = i3; d3 = d2; i3 = i2; d2 = dist; i2 = i; }
                    else if (dist < d3) { d4 = d3; i4 = i3; d3 = dist; i3 = i; }
                    else { d4 = dist; i4 = i; }
                }
            }

            int frauds = 0;
            if (d0 != int.MaxValue) frauds += _labels[i0];
            if (d1 != int.MaxValue) frauds += _labels[i1];
            if (d2 != int.MaxValue) frauds += _labels[i2];
            if (d3 != int.MaxValue) frauds += _labels[i3];
            if (d4 != int.MaxValue) frauds += _labels[i4];
            return frauds;
        }
    }

    /// <summary>Squared L2 between two 14-dim int8 vectors stored at Stride=16 byte alignment.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DistanceL2(sbyte* a, sbyte* b)
    {
        if (Avx2.IsSupported)
        {
            var av = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(a));
            var bv = Avx2.ConvertToVector256Int16(Sse2.LoadVector128(b));
            var diff = Avx2.Subtract(av, bv);
            var madd = Avx2.MultiplyAddAdjacent(diff, diff);
            return HSum256(madd);
        }
        int s = 0;
        for (int d = 0; d < Dims; d++)
        {
            int delta = a[d] - b[d];
            s += delta * delta;
        }
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HSum256(Vector256<int> v)
    {
        var lo = v.GetLower();
        var hi = v.GetUpper();
        var s = Sse2.Add(lo, hi);
        s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E));
        s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1));
        return s.ToScalar();
    }
}
