using Core.Database;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Immutable;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core;

public sealed partial class AddonReader : IAddonReader
{
    private readonly ILogger<AddonReader> logger;
    private readonly IAddonDataProvider reader;

    private readonly PlayerReader playerReader;
    private readonly CreatureDB creatureDb;

    private readonly CombatLog combatLog;
    private readonly TextReader textReader;

    private readonly ImmutableArray<IReader> readers;

    private readonly SpellBookReader spellBookReader;
    private readonly KeyBindingsReader keyBindingsReader;
    private readonly ActionBarTextureReader textureReader;

    private bool awaitingReinitialization;
    private bool refreshInProgress;
    private bool expectedResetObserved;
    private long reinitStartTime;

    public event Action? AddonDataChanged;

    public ManualResetEventSlim DataReady { get; }

    public RecordInt GlobalTime { get; }

    public int FullResetCount { get; private set; }
    public int ExpectedRefreshResetCount { get; private set; }
    public AddonResetReason? LastResetReason { get; private set; }
    public bool SpellBookInitialized => spellBookReader.IsInitialized;
    public bool TextureInitialized => textureReader.IsInitialized;
    public int BindingQueueRaw => reader.GetInt(106);
    public int SpellBookQueueRaw => reader.GetInt(71);
    public int TextureQueueRaw => reader.GetInt(107);

    private int previousGlobalTime;

    private int lastTargetGuid = -1;
    public string TargetName { get; private set; } = string.Empty;

    private int lastMouseOverId = -1;
    public string MouseOverName { get; private set; } = string.Empty;

    public double AvgUpdateLatency { private set; get; }

    public AddonReader(ILogger<AddonReader> logger,
        IAddonDataProvider reader,
        PlayerReader playerReader, ManualResetEventSlim resetEvent,
        CreatureDB creatureDb,
        CombatLog combatLog,
        TextReader textReader,
        SpellBookReader spellBookReader,
        KeyBindingsReader keyBindingsReader,
        ActionBarTextureReader textureReader,
        DataFrame[] frames,
        IServiceProvider sp)
    {
        this.logger = logger;
        this.reader = reader;
        this.creatureDb = creatureDb;
        this.combatLog = combatLog;
        this.textReader = textReader;
        this.playerReader = playerReader;
        this.spellBookReader = spellBookReader;
        this.keyBindingsReader = keyBindingsReader;
        this.textureReader = textureReader;
        DataReady = resetEvent;

        GlobalTime = new(frames.Length - 2);

        readers = sp.GetServices<IReader>().ToImmutableArray();
    }

    public void Update()
    {
        IAddonDataProvider reader = this.reader;
        reader.UpdateData();

        long lastUpdate = GlobalTime.LastChanged;

        if (!GlobalTime.Updated(reader))
            return;

        AvgUpdateLatency = GetElapsedTime(lastUpdate).TotalMilliseconds;

        bool inInitPhase = GlobalTime.Value < AddonTicks.INIT_PHASE;
        bool rolledBack = GlobalTime.Value < previousGlobalTime;
        if (inInitPhase || rolledBack)
        {
            if (refreshInProgress)
            {
                // Only the first low/rollback sample belongs to the flush we
                // explicitly requested. A later rollback means the addon
                // restarted again during the refresh and must still take the
                // normal production reset path.
                if (expectedResetObserved && rolledBack)
                {
                    int previousUnexpectedRollback = previousGlobalTime;
                    previousGlobalTime = GlobalTime.Value;
                    refreshInProgress = false;
                    ResetReaders(
                        AddonResetReason.GlobalTimeRollback,
                        previousUnexpectedRollback,
                        GlobalTime.Value);
                    return;
                }

                int previousExpected = previousGlobalTime;
                previousGlobalTime = GlobalTime.Value;
                if (!expectedResetObserved)
                {
                    expectedResetObserved = true;
                    ExpectedRefreshResetCount++;
                    LogExpectedRefreshReset(logger, previousExpected, GlobalTime.Value);
                }

                // BeginRefresh already reset every reader. The zero/init-phase
                // value emitted by the just-requested /dcflush is expected and
                // must not clear the queue readers a second time.
                return;
            }

            int previousUnexpected = previousGlobalTime;
            previousGlobalTime = GlobalTime.Value;
            ResetReaders(
                rolledBack
                    ? AddonResetReason.GlobalTimeRollback
                    : AddonResetReason.GlobalTimeInitPhase,
                previousUnexpected,
                GlobalTime.Value);
            return;
        }

        previousGlobalTime = GlobalTime.Value;

        ReadOnlySpan<IReader> span = readers.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            span[i].Update(reader);
        }

        if (lastTargetGuid != playerReader.TargetGuid)
        {
            lastTargetGuid = playerReader.TargetGuid;

            TargetName =
                creatureDb.Entries.TryGetValue(playerReader.TargetId, out Creature c)
                ? c.Name
                : textReader.LastTargetName;
        }

        if (lastMouseOverId != playerReader.MouseOverId)
        {
            lastMouseOverId = playerReader.MouseOverId;
            MouseOverName =
                creatureDb.Entries.TryGetValue(playerReader.MouseOverId, out Creature c)
                ? c.Name
                : string.Empty;
        }

        // After FullReset, wait for queue-based readers to reinitialize
        // before signaling DataReady. This pauses the GOAP agent until
        // spell book, bindings, and textures have been repopulated.
        if (awaitingReinitialization)
        {
            if (spellBookReader.IsInitialized &&
                keyBindingsReader.IsInitialized &&
                textureReader.IsInitialized)
            {
                awaitingReinitialization = false;
                refreshInProgress = false;
                expectedResetObserved = false;
                float elapsed = (float)GetElapsedTime(reinitStartTime).TotalSeconds;
                LogReinitComplete(logger, elapsed);
            }
            else
            {
                return;
            }
        }

        DataReady.Set();
    }

    public void SessionReset()
    {
        combatLog.Reset();
    }

    public void FullReset()
    {
        ResetReaders(AddonResetReason.Other, previousGlobalTime, GlobalTime.Value);
    }

    /// <summary>
    /// Starts the one official refresh transaction used by live diagnostics.
    /// The following low/init GlobalTime value is produced by the requested
    /// Lua /dcflush, so AddonReader.Update must observe it without resetting
    /// the readers a second time.
    /// </summary>
    public void BeginRefresh()
    {
        refreshInProgress = true;
        expectedResetObserved = false;
        ResetReaders(AddonResetReason.ManualRefresh, previousGlobalTime, GlobalTime.Value);
    }

    private void ResetReaders(AddonResetReason reason, int previous, int current)
    {
        ReadOnlySpan<IReader> span = readers.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            span[i].Reset();
        }

        DataReady.Reset();
        awaitingReinitialization = true;
        reinitStartTime = GetTimestamp();
        FullResetCount++;
        LastResetReason = reason;
        LogFullReset(logger, reason, previous, current);

        SessionReset();
    }

    public void UpdateUI()
    {
        AddonDataChanged?.Invoke();
    }

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "FullReset Reason={reason} Previous={previous} Current={current}: pausing bot until readers reinitialize")]
    static partial void LogFullReset(
        ILogger logger,
        AddonResetReason reason,
        int previous,
        int current);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Information,
        Message = "Expected refresh GlobalTime reset observed Previous={previous} Current={current}")]
    static partial void LogExpectedRefreshReset(ILogger logger, int previous, int current);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Information,
        Message = "Readers reinitialized after {elapsedSec:F1}s, resuming bot")]
    static partial void LogReinitComplete(ILogger logger, float elapsedSec);
}
