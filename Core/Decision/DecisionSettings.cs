using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Core.Decision;

public sealed record LayaSettings
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:8000";
    public int TimeoutMs { get; init; } = 250;
    public double MinimumConfidence { get; init; } = 0.7;
    public int MinimumIntervalMs { get; init; } = 300;
}

public sealed record DecisionConfiguration
{
    public DecisionMode Mode { get; init; } = DecisionMode.Local;
    public LayaSettings Laya { get; init; } = new();
    public bool AllowFlee { get; init; } = true;
}

public sealed class DecisionSettings
{
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string? path;
    private DecisionConfiguration current;

    public DecisionSettings(DecisionConfiguration? initial = null, string? path = null)
    {
        this.path = path;
        try { current = Validate(initial ?? new()); }
        catch (ArgumentException) { current = new(); }
        if (path is not null && File.Exists(path))
        {
            try
            {
                JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
                // Yesterday's Laya mode is today's independent AI mode.
                if (node?["Mode"]?.GetValue<string>() is string oldMode &&
                    oldMode.Equals("Laya", StringComparison.OrdinalIgnoreCase))
                    node["Mode"] = "AI";
                DecisionConfiguration? saved = node?.Deserialize<DecisionConfiguration>(FileOptions);
                if (saved is not null) current = Validate(saved);
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
            {
                // An invalid override must never stop the local bot from starting.
                current = new();
            }
        }
    }

    public DecisionConfiguration Current => System.Threading.Volatile.Read(ref current);
    public event Action<DecisionConfiguration>? Changed;

    public void Save(DecisionConfiguration value)
    {
        value = Validate(value);
        if (path is not null)
        {
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, FileOptions));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        System.Threading.Volatile.Write(ref current, value);
        Changed?.Invoke(value);
    }

    public static DecisionConfiguration Validate(DecisionConfiguration value)
    {
        if (!Enum.IsDefined(value.Mode)) throw new ArgumentException("Invalid decision mode.");
        LayaSettings laya = value.Laya ?? throw new ArgumentException("Laya settings are required.");
        if (!Uri.TryCreate(laya.BaseUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
            throw new ArgumentException("Laya URL must be a local HTTP address.");
        if (laya.TimeoutMs is < 50 or > 300000 || laya.MinimumIntervalMs is < 100 or > 10000 ||
            laya.MinimumConfidence is < 0 or > 1)
            throw new ArgumentException("Decision timing or confidence is out of range.");
        return value;
    }
}
