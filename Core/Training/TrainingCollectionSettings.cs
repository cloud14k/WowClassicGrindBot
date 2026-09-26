using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Core.Training;

/// <summary>Runtime value backed by the host's existing appsettings.json.</summary>
public sealed class TrainingCollectionSettings
{
    public const string ConfigurationKey = "Training:EnableTrainingDataCollection";
    private readonly string? configPath;
    private readonly object sync = new();
    private bool enabled;

    public TrainingCollectionSettings(bool enabled = false, string? configPath = null)
    {
        this.enabled = enabled;
        this.configPath = configPath;
    }

    public bool EnableTrainingDataCollection
    {
        get { lock (sync) return enabled; }
    }

    public event Action<bool>? Changed;

    public void SetEnabled(bool value)
    {
        lock (sync)
        {
            if (enabled == value) return;
            if (configPath is not null)
            {
                // Preserve JSONC comments and every unrelated host setting.
                string json = File.ReadAllText(configPath);
                const string pattern = "(\"EnableTrainingDataCollection\"\\s*:\\s*)(true|false)";
                Match match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                    throw new InvalidOperationException("Training setting is missing from appsettings.json.");
                json = json[..match.Groups[2].Index] + (value ? "true" : "false") +
                    json[(match.Groups[2].Index + match.Groups[2].Length)..];
                string temporary = configPath + ".training-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, json);
                    File.Move(temporary, configPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }

            enabled = value;
        }

        Changed?.Invoke(value);
    }
}
