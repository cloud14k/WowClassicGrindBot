using System.Threading;
using System.Threading.Tasks;

namespace Core.Decision;

// The Local branch is the existing GOAP loop. Only the AI branch calls Laya.
public sealed class DecisionManager(DecisionSettings settings, LayaDecisionProvider laya)
{
    public DecisionMode Mode => settings.Current.Mode;
    public Task<DecisionResult> DecideAsync(AIDecisionRequest request, CancellationToken token) =>
        laya.DecideAsync(request, token);
}
