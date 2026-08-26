namespace Guyabano.Llm.Prompting;

public interface IPromptLoader
{
    Task<string> LoadAsync(
        string promptName,
        CancellationToken cancellationToken = default);
}
