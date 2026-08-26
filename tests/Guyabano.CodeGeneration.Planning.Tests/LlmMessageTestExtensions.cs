using Penghou.Baize;

namespace Guyabano.CodeGeneration.Planning.Tests;

internal static class LlmMessageTestExtensions
{
    public static string Text(this LlmMessage message) =>
        message.Parts.OfType<LlmTextContent>().Single().Text;
}
