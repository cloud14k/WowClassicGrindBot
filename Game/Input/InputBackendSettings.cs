using System;
using System.IO;
using System.Text.Json;

namespace Game;

/// <summary>Persisted bot input selection. Changes are applied to the active input backend immediately.</summary>
public sealed class InputBackendSettings
{
    private readonly string path;
    public string Backend { get; private set; } = "Windows";
    public string Port { get; private set; } = "COM3";
    public event Action? Changed;

    public InputBackendSettings() : this(Path.Combine(AppContext.BaseDirectory, "input-backend.json")) { }
    public InputBackendSettings(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("Backend", out JsonElement backend) && backend.GetString() == "Hid") Backend = "Hid";
        if (root.TryGetProperty("Port", out JsonElement port) && !string.IsNullOrWhiteSpace(port.GetString())) Port = port.GetString()!;
    }

    public void Save(string backend, string port)
    {
        if (backend is not ("Windows" or "Hid")) throw new ArgumentException("Invalid input backend.", nameof(backend));
        if (string.IsNullOrWhiteSpace(port)) throw new ArgumentException("CDC port is required.", nameof(port));
        port = port.Trim();
        if (Backend == backend && Port.Equals(port, StringComparison.OrdinalIgnoreCase)) return;

        string oldBackend = Backend;
        string oldPort = Port;
        string json = JsonSerializer.Serialize(new { Backend = backend, Port = port });
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        try
        {
            Backend = backend;
            Port = port;
            Changed?.Invoke();
            File.Move(temporary, path, true);
        }
        catch
        {
            Backend = oldBackend;
            Port = oldPort;
            try { Changed?.Invoke(); } catch { }
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }
}
