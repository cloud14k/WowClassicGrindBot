using Core;
using Core.Decision;
using Game;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CoreTests;

internal static class Test_Decision
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task RunAsync(ILogger logger, ILoggerFactory loggerFactory, bool useDxgi, string[] args)
    {
        if (args.Contains("selftest", StringComparer.OrdinalIgnoreCase))
        {
            await SelfTestAsync(logger);
            return;
        }
        int file = Array.FindIndex(args, x => x.Equals("--file", StringComparison.OrdinalIgnoreCase));
        if (file >= 0 && file + 1 < args.Length)
        {
            AIObservation observation = JsonSerializer.Deserialize<AIObservation>(
                await File.ReadAllTextAsync(args[file + 1]), JsonOptions)
                ?? throw new InvalidDataException("Observation JSON is empty");
            await PrintDecisionAsync(observation, logger);
            return;
        }
        if (!GameTestEnvironment.TryCreate(logger, loggerFactory, useDxgi,
            out GameTestEnvironment? environment, out string reason))
        {
            logger.LogError("Decision reader unavailable: {Reason}", reason);
            Environment.ExitCode = 1;
            return;
        }
        using (environment)
        {
            IServiceProvider services = environment!.Services;
            IWowScreen screen = services.GetRequiredService<IWowScreen>();
            AddonReader addon = services.GetRequiredService<AddonReader>();
            PlayerReader reader = services.GetRequiredService<PlayerReader>();
            AddonBits bits = services.GetRequiredService<AddonBits>();
            environment.MarkReaderGraphInitialized();
            screen.Enabled = true;
            do
            {
                screen.Update();
                addon.Update();
                bool target = bits.Target();
                await PrintDecisionAsync(new AIObservation
                {
                    PlayerHP = reader.HealthPercent(), Mana = reader.ManaPercent(),
                    PlayerAlive = !bits.Dead(), HasTarget = target,
                    TargetAlive = target && bits.Target_Alive(),
                    TargetHostile = target && bits.Target_Hostile(),
                    TargetHP = target ? reader.TargetHealthPercent() : 0,
                    TargetDistance = target ? (reader.MinRange() + reader.MaxRange()) / 2f : 0,
                    TargetTargetsMe = target && reader.TargetsMe(), AutoShotActive = bits.AutoShot(),
                    RangedSwingElapsedMs = reader.AutoShot.ElapsedMs(),
                    MainHandSwingElapsedMs = reader.MainHandSwing.ElapsedMs(), Moving = bits.Moving()
                }, logger);
                if (!args.Contains("watch", StringComparer.OrdinalIgnoreCase)) break;
                await Task.Delay(1000);
            } while (true);
        }
    }

    private static async Task PrintDecisionAsync(AIObservation observation, ILogger logger)
    {
        using HttpClient client = new();
        DecisionSettings settings = new(new DecisionConfiguration { Mode = DecisionMode.AI });
        DecisionManager manager = new(settings, new LayaDecisionProvider(client, settings));
        AIDecisionRequest request = new("Continue the grind route", observation, [], null,
            Enum.GetValues<ActionIntent>());
        try
        {
            DecisionResult result = await manager.DecideAsync(request, CancellationToken.None);
            logger.LogInformation("DecisionMode=AI ExecutedSource=Preview Action={Action} Confidence={Confidence} LatencyMs={LatencyMs}",
                result.Action, result.Confidence, result.LatencyMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DecisionMode=AI AI service unavailable; no Local fallback");
        }
    }

    private static async Task SelfTestAsync(ILogger logger)
    {
        DecisionSettings settings = new(new DecisionConfiguration { Mode = DecisionMode.AI });
        AIDecisionRequest request = new("Test", new AIObservation(), [], null, [ActionIntent.Wait]);
        async Task Check(string response, HttpStatusCode code, bool succeeds)
        {
            using HttpClient client = new(new FakeHandler(response, code));
            DecisionManager manager = new(settings, new LayaDecisionProvider(client, settings));
            bool threw = false;
            try
            {
                DecisionResult result = await manager.DecideAsync(request, CancellationToken.None);
                if (succeeds && result.Action != ActionIntent.Wait) throw new Exception("Unexpected response");
            }
            catch (Exception) when (!succeeds) { threw = true; }
            if (!succeeds && !threw) throw new Exception("Expected AI failure");
        }
        await Check("{\"action\":\"Wait\",\"confidence\":0.9}", HttpStatusCode.OK, true);
        await Check("{\"action\":\"Unknown\",\"confidence\":0.9}", HttpStatusCode.OK, false);
        await Check("{\"action\":\"Wait\",\"confidence\":0.9}", HttpStatusCode.InternalServerError, false);
        await Check("invalid", HttpStatusCode.OK, false);
        using (HttpClient refused = new(new FailingHandler()))
        {
            DecisionManager manager = new(settings, new LayaDecisionProvider(refused, settings));
            bool failed = false;
            try { await manager.DecideAsync(request, CancellationToken.None); }
            catch (HttpRequestException) { failed = true; }
            if (!failed || manager.Mode != DecisionMode.AI)
                throw new Exception("Connection failure changed decision mode or fell back");
        }
        using (HttpClient recovered = new(new FlakyHandler()))
        {
            DecisionManager manager = new(settings, new LayaDecisionProvider(recovered, settings));
            bool firstFailed = false;
            try { await manager.DecideAsync(request, CancellationToken.None); }
            catch (HttpRequestException) { firstFailed = true; }
            DecisionResult resumed = await manager.DecideAsync(request, CancellationToken.None);
            if (!firstFailed || manager.Mode != DecisionMode.AI || resumed.Action != ActionIntent.Wait)
                throw new Exception("AI did not recover without changing mode");
        }
        logger.LogInformation("AI decision selftest passed: valid, invalid action, HTTP error, JSON error, connection failure and recovery without Local fallback");
    }

    private sealed class FakeHandler(string response, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("Connection refused");
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new HttpRequestException("Connection refused");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"action\":\"Wait\",\"confidence\":0.9}", Encoding.UTF8, "application/json")
            });
        }
    }
}
