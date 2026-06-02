using System.Runtime.CompilerServices;

namespace FraudApi;

public static class Featurize
{
    public const int Dims = 14;
    public const int Stride = 16;
    public const sbyte Sentinel = -127;
    public const double QScale = 127.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte Q(double v)
    {
        var x = (int)Math.Round(v * QScale);
        if (x > 127) x = 127;
        else if (x < -127) x = -127;
        return (sbyte)x;
    }

    public static void Quantize(ReadOnlySpan<float> raw, Span<sbyte> dst)
    {
        for (int i = 0; i < Dims; i++)
        {
            float v = raw[i];
            if (v == -1f)
            {
                dst[i] = Sentinel;
                continue;
            }
            if (v < 0) v = 0;
            else if (v > 1) v = 1;
            int x = (int)MathF.Round(v * 127f);
            dst[i] = (sbyte)x;
        }
        for (int i = Dims; i < Stride; i++) dst[i] = 0;
    }

    public static void Build(TxRequest req, IReadOnlyDictionary<string, double> mccRisk, Span<sbyte> dst)
    {
        var tx = req.Transaction ?? new TxTransaction();
        var cust = req.Customer ?? new TxCustomer();
        var merch = req.Merchant ?? new TxMerchant();
        var term = req.Terminal ?? new TxTerminal();
        var last = req.LastTransaction;

        double v0 = Clamp01(tx.Amount / 10000.0);
        double v1 = Clamp01(tx.Installments / 12.0);
        double v2 = cust.AvgAmount > 0 ? Clamp01((tx.Amount / cust.AvgAmount) / 10.0) : 1.0;

        // Parse the request timestamp exactly once. Format is fixed ISO 8601: "yyyy-MM-ddTHH:mm:ssZ".
        bool hasReq = TryParseFastIso(tx.RequestedAt, out long reqTicks, out int reqHour, out int reqDow);
        double v3 = 0, v4 = 0;
        if (hasReq)
        {
            v3 = reqHour / 23.0;
            v4 = (reqDow == 0 ? 6 : reqDow - 1) / 6.0;     // Sunday(0)→6, Mon..Sat→0..5
        }

        double v5, v6;
        if (last is null || !hasReq
            || !TryParseFastIso(last.Timestamp, out long lastTicks, out _, out _))
        {
            v5 = -1; v6 = -1;
        }
        else
        {
            // 10_000 ticks per ms × 60_000 ms per minute = 600_000_000 ticks per minute
            double minutes = Math.Abs(reqTicks - lastTicks) / 600_000_000.0;
            v5 = Clamp01(minutes / 1440.0);
            v6 = Clamp01(last.KmFromCurrent / 1000.0);
        }

        double v7 = Clamp01(term.KmFromHome / 1000.0);
        double v8 = Clamp01(cust.TxCount24h / 20.0);
        double v9 = term.IsOnline ? 1.0 : 0.0;
        double v10 = term.CardPresent ? 1.0 : 0.0;
        double v11 = IsUnknown(merch.Id, cust.KnownMerchants) ? 1.0 : 0.0;
        double v12 = (merch.Mcc != null && mccRisk.TryGetValue(merch.Mcc, out var risk)) ? risk : 0.5;
        double v13 = Clamp01(merch.AvgAmount / 10000.0);

        dst[0] = Q(v0);
        dst[1] = Q(v1);
        dst[2] = Q(v2);
        dst[3] = Q(v3);
        dst[4] = Q(v4);
        dst[5] = v5 < 0 ? Sentinel : Q(v5);
        dst[6] = v6 < 0 ? Sentinel : Q(v6);
        dst[7] = Q(v7);
        dst[8] = Q(v8);
        dst[9] = Q(v9);
        dst[10] = Q(v10);
        dst[11] = Q(v11);
        dst[12] = Q(v12);
        dst[13] = Q(v13);
        dst[14] = 0;
        dst[15] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnknown(string? merchantId, string[]? known)
    {
        if (merchantId is null || known is null || known.Length == 0) return true;
        for (int i = 0; i < known.Length; i++)
            if (string.Equals(known[i], merchantId, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Fast parser for the fixed ISO 8601 format "yyyy-MM-ddTHH:mm:ssZ" (20 chars).
    /// Returns ticks (UTC), hour-of-day, and day-of-week (0=Sun..6=Sat) without allocating.
    /// 10-20x faster than DateTime.TryParse on the hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseFastIso(string? s, out long ticks, out int hour, out int dayOfWeek)
    {
        ticks = 0; hour = 0; dayOfWeek = 0;
        if (s is null || s.Length < 20) return false;

        // Positions:        0123 4 56 7 89 0 12 3 45 6 78
        //                   yyyy - MM - dd T HH : mm : ss Z
        int year, month, day, minute, second;
        if (!TwoDigits(s, 0, out int y2) || !TwoDigits(s, 2, out int y1)) return false;
        year = y2 * 100 + y1;
        if (s[4] != '-' || !TwoDigits(s, 5, out month) || s[7] != '-' || !TwoDigits(s, 8, out day)) return false;
        if (s[10] != 'T') return false;
        if (!TwoDigits(s, 11, out hour) || s[13] != ':' || !TwoDigits(s, 14, out minute)
            || s[16] != ':' || !TwoDigits(s, 17, out second)) return false;

        try
        {
            var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            ticks = dt.Ticks;
            dayOfWeek = (int)dt.DayOfWeek;
            return true;
        }
        catch { return false; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TwoDigits(string s, int idx, out int value)
    {
        int a = s[idx] - '0', b = s[idx + 1] - '0';
        if ((uint)a > 9 || (uint)b > 9) { value = 0; return false; }
        value = a * 10 + b;
        return true;
    }
}
