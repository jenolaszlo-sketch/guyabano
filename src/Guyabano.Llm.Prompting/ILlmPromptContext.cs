namespace Guyabano.Llm.Prompting;

public interface ILlmPromptContext
{
    double Temperature { get; }
    int MaxTokens { get; }
}