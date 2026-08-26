using Penghou.Baize;
using Penghou.Baize.Tools;

namespace Guyabano.CodeGeneration.Planning;

internal static class ArchitecturePlanningFailureMapper
{
    public static PlanningFailure Map(ToolCallParseFailure failure) =>
        failure switch
        {
            ToolCallParseFailure.MissingToolCall =>
                PlanningFailure.MissingToolCall,
            ToolCallParseFailure.EmptyArguments or
            ToolCallParseFailure.InvalidJson =>
                PlanningFailure.InvalidToolArguments,
            ToolCallParseFailure.TruncatedResponse =>
                PlanningFailure.TruncatedResponse,
            ToolCallParseFailure.SchemaValidationFailed =>
                PlanningFailure.SchemaValidationFailed,
            ToolCallParseFailure.DeserializationFailed =>
                PlanningFailure.DeserializationFailed,
            _ => PlanningFailure.InvalidToolArguments
        };

    public static (bool Repaired, IReadOnlyList<LlmRepairAttempt> Attempts)
        GetRepairInfo(LlmResponse normalized, string toolName)
    {
        var call = normalized.ToolCalls?.FirstOrDefault(item =>
            item.Name.Equals(toolName, StringComparison.Ordinal));
        return (
            call?.JsonWasRepaired ?? false,
            call?.JsonRepairAttempts ?? []);
    }
}
