using Core.Decision;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CoreTests;

internal static class Test_AiTest
{
    // Laya with --no-preload can fetch and initialize model weights on the first request.
    private const int RequestTimeoutSeconds = 300;
    private const string LayaTypedModel = "typed-decisions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task RunAsync(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        int offlineIndex = Array.FindIndex(args,
            value => value.Equals("--offline", StringComparison.OrdinalIgnoreCase));
        if (offlineIndex < 0)
            return Test_AiGrind.RunAsync(logger, loggerFactory, useDxgi, args);

        string[] offlineArgs = args.Where((_, index) => index != offlineIndex).ToArray();
        return RunOfflineAsync(logger, offlineArgs);
    }

    private static async Task RunOfflineAsync(ILogger logger, string[] args)
    {
        if (!TryReadArguments(args, out string aiName, out Uri endpoint,
            out string subset, out string argumentError))
        {
            logger.LogError("AI test arguments invalid: {Reason}", argumentError);
            LogUsage(logger);
            Environment.ExitCode = 2;
            return;
        }

        AiTestCase[] cases;
        try
        {
            cases = await LoadSubsetAsync(subset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.LogError(ex, "AI test subset {Subset} could not be loaded", subset);
            Environment.ExitCode = 2;
            return;
        }

        if (cases.Length == 0)
        {
            logger.LogError("AI test subset {Subset} contains no cases", subset);
            Environment.ExitCode = 2;
            return;
        }

        logger.LogInformation(
            "AI test started: AI={AiName} Endpoint={Endpoint} Subset={Subset} Cases={CaseCount} TimeoutSeconds={TimeoutSeconds}",
            aiName, endpoint.GetLeftPart(UriPartial.Path), subset, cases.Length, RequestTimeoutSeconds);
        logger.LogInformation("AI test uses the command-line endpoint directly; Decision settings are not loaded");

        int passed = 0;
        int failed = 0;
        bool layaPredict = IsLayaPredictEndpoint(endpoint);
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };

        foreach (AiTestCase testCase in cases)
        {
            object request = layaPredict
                ? CreateLayaPredictRequest(testCase)
                : new AIDecisionRequest(testCase.Objective, testCase.Observation, [], null,
                    testCase.AvailableCapabilities);
            logger.LogInformation("AI test case started: AI={AiName} Case={CaseName} Expected={ExpectedActions} Request={Request}",
                aiName, testCase.Name, string.Join("|", testCase.ExpectedActions),
                JsonSerializer.Serialize(request, JsonOptions));

            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(RequestTimeoutSeconds));
                using HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, request, JsonOptions, timeout.Token);
                string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    failed++;
                    logger.LogError(
                        "AI test case FAIL: AI={AiName} Case={CaseName} HttpStatus={StatusCode} Response={Response}",
                        aiName, testCase.Name, (int)response.StatusCode, responseBody);
                    continue;
                }

                bool parsed = layaPredict
                    ? TryReadLayaDecision(responseBody, out ActionIntent action, out double confidence, out string parseError)
                    : TryReadDecision(responseBody, out action, out confidence, out parseError);
                if (!parsed)
                {
                    failed++;
                    logger.LogError("AI test case FAIL: AI={AiName} Case={CaseName} InvalidResponse={Reason} Body={Response}",
                        aiName, testCase.Name, parseError, responseBody);
                    continue;
                }

                bool actionAccepted = testCase.ExpectedActions.Contains(action);
                if (actionAccepted)
                {
                    passed++;
                    logger.LogInformation(
                        "AI test case PASS: AI={AiName} Case={CaseName} Action={Action} Confidence={Confidence}",
                        aiName, testCase.Name, action, confidence);
                }
                else
                {
                    failed++;
                    logger.LogError(
                        "AI test case FAIL: AI={AiName} Case={CaseName} Action={Action} Expected={ExpectedActions} Confidence={Confidence}",
                        aiName, testCase.Name, action, string.Join("|", testCase.ExpectedActions), confidence);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                failed++;
                logger.LogError(ex, "AI test case FAIL: AI={AiName} Case={CaseName} endpoint request failed",
                    aiName, testCase.Name);
            }
        }

        logger.LogInformation("AI test finished: AI={AiName} Subset={Subset} Passed={Passed} Failed={Failed} Total={Total}",
            aiName, subset, passed, failed, passed + failed);
        if (failed > 0)
            Environment.ExitCode = 1;
    }

    private static bool TryReadArguments(string[] args, out string aiName, out Uri endpoint,
        out string subset, out string error)
    {
        aiName = string.Empty;
        endpoint = null;
        subset = "overall";
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Missing value for {args[index]}";
                return false;
            }

            string value = args[++index];
            switch (args[index - 1].ToLowerInvariant())
            {
                case "--ai-name":
                    aiName = value.Trim();
                    break;
                case "--endpoint":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint) ||
                        endpoint.Scheme is not ("http" or "https"))
                    {
                        error = "--endpoint must be an absolute HTTP or HTTPS URL";
                        return false;
                    }
                    break;
                case "--subset":
                    subset = value.Trim();
                    break;
                default:
                    error = $"Unknown option: {args[index - 1]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(aiName))
        {
            error = "--ai-name is required";
            return false;
        }
        if (endpoint is null)
        {
            error = "--endpoint is required";
            return false;
        }
        if (string.IsNullOrWhiteSpace(subset))
        {
            error = "--subset cannot be empty";
            return false;
        }
        return true;
    }

    private static bool IsLayaPredictEndpoint(Uri endpoint) =>
        endpoint.AbsolutePath.TrimEnd('/').EndsWith("/predict", StringComparison.OrdinalIgnoreCase);

    private static object CreateLayaPredictRequest(AiTestCase testCase)
    {
        Dictionary<string, string> criteria = [];
        foreach (ActionIntent action in testCase.AvailableCapabilities)
            criteria[action.ToString()] = ActionDescriptions[action];

        return new
        {
            model = LayaTypedModel,
            state = new
            {
                testCase.Objective,
                observation = testCase.Observation,
                availableCapabilities = testCase.AvailableCapabilities
            },
            questions = new Dictionary<string, object>
            {
                ["action"] = new
                {
                    type = "choice",
                    instructions = "Choose the best next game action that is legal and advances the objective. Select only one of the available action options.",
                    criteria
                }
            }
        };
    }

    private static readonly IReadOnlyDictionary<ActionIntent, string> ActionDescriptions =
        new Dictionary<ActionIntent, string>
        {
            [ActionIntent.Wait] = "Wait without changing the current situation.",
            [ActionIntent.AcquireTarget] = "Select a nearby hostile target when no valid target is selected.",
            [ActionIntent.ApproachTarget] = "Move closer to a living target that is outside attack range.",
            [ActionIntent.MoveAwayFromTarget] = "Move backward to create distance from a dangerous target.",
            [ActionIntent.StopMovement] = "Stop current movement.",
            [ActionIntent.StartAutoShot] = "Start ranged auto-attacks against a living hostile target.",
            [ActionIntent.CastRaptorStrike] = "Use the ready melee ability against a nearby hostile target.",
            [ActionIntent.ContinueCurrentAction] = "Continue an action that is already in progress.",
            [ActionIntent.Flee] = "Move to a safe location because survival is the priority.",
            [ActionIntent.Loot] = "Interact with a nearby defeated target to loot it.",
            [ActionIntent.ContinueRoute] = "Continue following the configured grind route."
        };

    private static bool TryReadLayaDecision(string body, out ActionIntent action,
        out double confidence, out string error)
    {
        action = default;
        confidence = 0;
        error = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("answers", out JsonElement answers) ||
                !answers.TryGetProperty("action", out JsonElement actionAnswer) ||
                !actionAnswer.TryGetProperty("choice", out JsonElement choice) ||
                choice.ValueKind != JsonValueKind.String ||
                !Enum.TryParse(choice.GetString(), true, out action) || !Enum.IsDefined(action))
            {
                error = "Laya response must contain answers.action.choice with a recognized action";
                return false;
            }

            if (!(actionAnswer.TryGetProperty("answer_confidence", out JsonElement confidenceValue) ||
                  actionAnswer.TryGetProperty("confidence", out confidenceValue)) ||
                !confidenceValue.TryGetDouble(out confidence) ||
                !double.IsFinite(confidence) || confidence is < 0 or > 1)
            {
                error = "Laya response must contain answers.action.answer_confidence between 0 and 1";
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = "Response is not valid JSON";
            return false;
        }
    }

    private static async Task<AiTestCase[]> LoadSubsetAsync(string subset)
    {
        string path = subset.Equals("overall", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "ai-tests", "overall.json")
            : Path.GetFullPath(subset);
        string content = await File.ReadAllTextAsync(path);
        AiTestCase[] cases = JsonSerializer.Deserialize<AiTestCase[]>(content, JsonOptions)
            ?? throw new JsonException("Subset file must contain a JSON array");

        foreach (AiTestCase testCase in cases)
        {
            if (string.IsNullOrWhiteSpace(testCase.Name) || string.IsNullOrWhiteSpace(testCase.Objective) ||
                testCase.AvailableCapabilities.Length == 0 || testCase.ExpectedActions.Length == 0)
                throw new JsonException("Each test case needs a name, objective, availableCapabilities and expectedActions");
        }
        return cases;
    }

    private static bool TryReadDecision(string body, out ActionIntent action, out double confidence, out string error)
    {
        action = default;
        confidence = 0;
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!(root.TryGetProperty("action", out JsonElement actionElement) ||
                  root.TryGetProperty("Action", out actionElement)) ||
                actionElement.ValueKind != JsonValueKind.String ||
                !Enum.TryParse(actionElement.GetString(), true, out action) || !Enum.IsDefined(action))
            {
                error = "Response must contain a recognized action";
                return false;
            }

            if (!(root.TryGetProperty("confidence", out JsonElement confidenceElement) ||
                  root.TryGetProperty("Confidence", out confidenceElement)) ||
                !confidenceElement.TryGetDouble(out confidence) ||
                !double.IsFinite(confidence) || confidence is < 0 or > 1)
            {
                error = "Response must contain confidence between 0 and 1";
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = "Response is not valid JSON";
            return false;
        }
    }

    private static void LogUsage(ILogger logger) =>
        logger.LogInformation("Usage: ai-test --ai-name <name> --endpoint <url> [--subset overall|<json-file>]");

    private sealed record AiTestCase(
        string Name,
        string Objective,
        AIObservation Observation,
        ActionIntent[] AvailableCapabilities,
        ActionIntent[] ExpectedActions);
}
