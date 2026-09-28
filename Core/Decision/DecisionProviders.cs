using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Decision;

public sealed class LayaDecisionProvider(HttpClient client, DecisionSettings settings)
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DecisionResult> DecideAsync(AIDecisionRequest request, CancellationToken cancellationToken)
    {
        LayaSettings options = settings.Current.Laya;
        bool predictEndpoint = options.BaseUrl.TrimEnd('/').EndsWith("/predict", StringComparison.OrdinalIgnoreCase);
        Uri endpoint = predictEndpoint
            ? new Uri(options.BaseUrl)
            : new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), "decision");
        object payload = predictEndpoint ? CreatePredictRequest(request) : request;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.TimeoutMs);
        Stopwatch watch = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            endpoint, payload, WireOptions, timeout.Token);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        JsonElement root = json.RootElement;
        if (predictEndpoint)
        {
            if (!root.TryGetProperty("answers", out JsonElement answers) ||
                !answers.TryGetProperty("action", out JsonElement actionAnswer) ||
                !actionAnswer.TryGetProperty("choice", out JsonElement choice) ||
                choice.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Laya returned no recognized answers.action.choice.");

            string choiceId = choice.GetString()!;
            AIConfiguredSkill? selectedSkill = request.AvailableSkills?.FirstOrDefault(
                skill => skill.Id.Equals(choiceId, StringComparison.Ordinal));
            ActionIntent layaAction;
            if (selectedSkill is not null)
                layaAction = ActionIntent.CastConfiguredSkill;
            else if (!Enum.TryParse(choiceId, true, out layaAction) ||
                     !Enum.IsDefined(layaAction) || layaAction == ActionIntent.CastConfiguredSkill)
                throw new InvalidOperationException($"Laya returned unknown action '{choiceId}'.");

            if (!(actionAnswer.TryGetProperty("answer_confidence", out JsonElement answerConfidence) ||
                  actionAnswer.TryGetProperty("confidence", out answerConfidence)) ||
                !answerConfidence.TryGetDouble(out double confidence) ||
                !double.IsFinite(confidence) || confidence < options.MinimumConfidence || confidence > 1)
                throw new InvalidOperationException("Laya returned missing, invalid, or below-threshold action confidence.");

            return new(layaAction, confidence, watch.ElapsedMilliseconds, selectedSkill?.Id);
        }

        if (!(root.TryGetProperty("action", out JsonElement actionElement) ||
              root.TryGetProperty("Action", out actionElement)) ||
            actionElement.ValueKind != JsonValueKind.String ||
            !Enum.TryParse(actionElement.GetString(), true, out ActionIntent action) ||
            !Enum.IsDefined(action))
            throw new InvalidOperationException("Laya returned an unknown action.");
        if (!(root.TryGetProperty("confidence", out JsonElement confidenceElement) ||
              root.TryGetProperty("Confidence", out confidenceElement)) ||
              !confidenceElement.TryGetDouble(out double directConfidence) ||
              !double.IsFinite(directConfidence) || directConfidence < options.MinimumConfidence || directConfidence > 1)
            throw new InvalidOperationException("Laya confidence is missing or below the threshold.");
        return new(action, directConfidence, watch.ElapsedMilliseconds);
    }

    private static object CreatePredictRequest(AIDecisionRequest request)
    {
        Dictionary<string, string> criteria = [];
        foreach (ActionIntent action in request.AvailableCapabilities)
            if (action != ActionIntent.CastConfiguredSkill)
                criteria[action.ToString()] = ActionDescriptions[action];
        foreach (AIConfiguredSkill skill in request.AvailableSkills ?? [])
            criteria[skill.Id] = skill.Description;

        return new
        {
            model = "typed-decisions",
            state = new
            {
                request.Objective,
                observation = request.CurrentObservation,
                recentHistory = request.RecentHistory,
                lastActionResult = request.LastActionResult,
                availableCapabilities = request.AvailableCapabilities,
                availableSkills = request.AvailableSkills
            },
            questions = new Dictionary<string, object>
            {
                ["action"] = new
                {
                    type = "choice",
                    instructions = request.Question,
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
            [ActionIntent.ApproachTarget] = "Move closer to a living hostile target.",
            [ActionIntent.MoveAwayFromTarget] = "Move backward to create distance from a dangerous target.",
            [ActionIntent.MaintainRange] = "Backpedal when a hunter target is too close; restore ranged shooting distance before attacking again.",
            [ActionIntent.StopMovement] = "Stop current movement.",
            [ActionIntent.StartAutoShot] = "Start ranged auto-attacks against a living hostile target.",
            [ActionIntent.CastRaptorStrike] = "Use the ready melee ability against a nearby hostile target.",
            [ActionIntent.PetAttack] = "Send the pet to attack the living hostile target.",
            [ActionIntent.ContinueCurrentAction] = "Continue an action that is already in progress.",
            [ActionIntent.Flee] = "Move to a safe location because survival is the priority.",
            [ActionIntent.Loot] = "Find and loot the corpse from the most recent kill, approaching it if needed.",
            [ActionIntent.ContinueRoute] = "Continue following the configured grind route."
        };
}
