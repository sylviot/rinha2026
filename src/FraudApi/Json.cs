using System.Text.Json.Serialization;

namespace FraudApi;

public sealed class TxRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("transaction")] public TxTransaction? Transaction { get; set; }
    [JsonPropertyName("customer")] public TxCustomer? Customer { get; set; }
    [JsonPropertyName("merchant")] public TxMerchant? Merchant { get; set; }
    [JsonPropertyName("terminal")] public TxTerminal? Terminal { get; set; }
    [JsonPropertyName("last_transaction")] public TxLast? LastTransaction { get; set; }
}

public sealed class TxTransaction
{
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("installments")] public int Installments { get; set; }
    [JsonPropertyName("requested_at")] public string? RequestedAt { get; set; }
}

public sealed class TxCustomer
{
    [JsonPropertyName("avg_amount")] public double AvgAmount { get; set; }
    [JsonPropertyName("tx_count_24h")] public int TxCount24h { get; set; }
    [JsonPropertyName("known_merchants")] public string[]? KnownMerchants { get; set; }
}

public sealed class TxMerchant
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("mcc")] public string? Mcc { get; set; }
    [JsonPropertyName("avg_amount")] public double AvgAmount { get; set; }
}

public sealed class TxTerminal
{
    [JsonPropertyName("is_online")] public bool IsOnline { get; set; }
    [JsonPropertyName("card_present")] public bool CardPresent { get; set; }
    [JsonPropertyName("km_from_home")] public double KmFromHome { get; set; }
}

public sealed class TxLast
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("km_from_current")] public double KmFromCurrent { get; set; }
}

public sealed class FraudResponse
{
    [JsonPropertyName("approved")] public bool Approved { get; set; }
    [JsonPropertyName("fraud_score")] public double FraudScore { get; set; }
}

public sealed class NormalizationConfig
{
    [JsonPropertyName("max_amount")] public double MaxAmount { get; set; } = 10000;
    [JsonPropertyName("max_installments")] public double MaxInstallments { get; set; } = 12;
    [JsonPropertyName("amount_vs_avg_ratio")] public double AmountVsAvgRatio { get; set; } = 10;
    [JsonPropertyName("max_minutes")] public double MaxMinutes { get; set; } = 1440;
    [JsonPropertyName("max_km")] public double MaxKm { get; set; } = 1000;
    [JsonPropertyName("max_tx_count_24h")] public double MaxTxCount24h { get; set; } = 20;
    [JsonPropertyName("max_merchant_avg_amount")] public double MaxMerchantAvgAmount { get; set; } = 10000;
}

[JsonSerializable(typeof(TxRequest))]
[JsonSerializable(typeof(FraudResponse))]
[JsonSerializable(typeof(NormalizationConfig))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
public partial class AppJsonContext : JsonSerializerContext { }
