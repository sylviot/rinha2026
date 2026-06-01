using System.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FraudApi;

// PREPROCESS_ONLY=1 → build refs.bin and exit (used during docker build).
if (Environment.GetEnvironmentVariable("PREPROCESS_ONLY") == "1")
{
    Directory.CreateDirectory(Preprocess.DataDir);
    Preprocess.BuildBinary();
    Console.WriteLine("[preprocess-only] done");
    return;
}

// State shared between background preprocess and request handlers.
var state = new AppState();

// Kick off preprocess in the background so Kestrel can bind :8080 immediately
// and nginx upstream connects succeed. Endpoints return 503 until ready.
_ = Task.Run(async () =>
{
    try
    {
        await Preprocess.EnsureAsync(CancellationToken.None);
        state.MccRisk = Preprocess.LoadMccRisk();
        var ds = new Dataset(Preprocess.GetActivePath());
        Console.WriteLine($"[startup] dataset loaded: {ds.Count:N0} vectors, {ds.NumCentroids} centroids, AVX2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        var t0 = Environment.TickCount64;
        var warmupSum = ds.WarmUp();
        Console.WriteLine($"[startup] mmap warmup {Environment.TickCount64 - t0}ms (sum={warmupSum})");
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        state.Dataset = ds;
        state.Ready = true;
        Console.WriteLine("[startup] ready");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[startup] preprocess failed: {ex}");
        Environment.Exit(1);
    }
});

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();

builder.WebHost.ConfigureKestrel(o =>
{
    o.AllowSynchronousIO = false;
    o.Limits.MaxConcurrentConnections = 1024;
    o.Limits.MaxConcurrentUpgradedConnections = 0;
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    o.ListenAnyIP(8080);
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

app.MapGet("/ready", () => state.Ready ? Results.Ok() : Results.StatusCode(503));

app.MapPost("/fraud-score", (TxRequest req) =>
{
    var ds = state.Dataset;
    var mcc = state.MccRisk;
    if (ds is null || mcc is null) return Results.StatusCode(503);
    Span<sbyte> q = stackalloc sbyte[Featurize.Stride];
    Featurize.Build(req, mcc, q);
    double score = ds.Score(q);
    return Results.Json(new FraudResponse { Approved = score < 0.6, FraudScore = score },
        AppJsonContext.Default.FraudResponse);
});

// Diagnostic: run N internal scoring iterations against a zero-query and
// report timing. Usage: curl localhost:9999/benchmark?n=200
app.MapGet("/benchmark", (int? n) =>
{
    var ds = state.Dataset;
    if (ds is null) return Results.StatusCode(503);
    int iters = n ?? 100;
    Span<sbyte> q = stackalloc sbyte[Featurize.Stride];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    double s = 0;
    for (int i = 0; i < iters; i++) s += ds.Score(q);
    sw.Stop();
    return Results.Text(
        $"iters={iters} total_ms={sw.Elapsed.TotalMilliseconds:F1} " +
        $"avg_ms={sw.Elapsed.TotalMilliseconds / iters:F2} " +
        $"qps={iters * 1000.0 / sw.Elapsed.TotalMilliseconds:F0} " +
        $"sum={s:F2}\n");
});

app.Run();

internal sealed class AppState
{
    public volatile bool Ready;
    public Dataset? Dataset;
    public IReadOnlyDictionary<string, double>? MccRisk;
}
