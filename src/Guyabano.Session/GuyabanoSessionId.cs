namespace Guyabano.Session;

public readonly record struct GuyabanoSessionId(Guid Value)
{
    public static GuyabanoSessionId New() => new(Guid.CreateVersion7());

    public static GuyabanoSessionId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new GuyabanoSessionId(Guid.Parse(value));
    }

    public override string ToString() => Value.ToString("D");
}
