using System.Threading;

namespace Guyabano.Llm.Prompting;

/// <summary>Ambient disclosure selected for exactly one Baize invocation path.</summary>
public static class SessionContextDisclosureScope
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current => CurrentValue.Value;

    public static IDisposable Push(string? content)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = content;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}
