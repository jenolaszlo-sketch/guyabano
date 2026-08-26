namespace Guyabano.Llm.Prompting;

public sealed record PromptTemplate(
    string SystemPromptName,
    string UserTemplateName);
