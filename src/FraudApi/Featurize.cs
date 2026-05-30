using System.Globalization;
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

        double v3 = 0, v4 = 0;
        if (TryParseIso(tx.RequestedAt, out var reqAt))
        {
            v3 = reqAt.Hour / 23.0;
            v4 = ((int)reqAt.DayOfWeek == 0 ? 6 : (int)reqAt.DayOfWeek - 1) / 6.0;
        }

        double v5, v6;
        if (last is null || string.IsNullOrEmpty(last.Timestamp) || !TryParseIso(last.Timestamp, out var lastAt))
        {
            v5 = -1; v6 = -1;
        }
        else
        {
            double minutes = TryParseIso(tx.RequestedAt, out var r2)
                ? Math.Abs((r2 - lastAt).TotalMinutes)
                : 0;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseIso(string? s, out DateTime dt)
    {
        if (string.IsNullOrEmpty(s)) { dt = default; return false; }
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt);
    }
}
