using System.Text;
using Guyabano.Llm.Prompting;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>Renders a stage catalogue for planner consumption.</summary>
public static class PlanningStageCatalogueSummary
{
    public static string Render(PlanningStageCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        var builder = new StringBuilder();
        foreach (var definition in catalogue.Definitions)
        {
            builder.Append("- ");
            builder.Append(definition.Id);
            builder.Append(" produces ");
            builder.Append(definition.ArtifactKind);
            builder.Append(" consumes ");
            builder.Append(definition.InputKinds.Count == 0
                ? "(none)"
                : string.Join(", ", definition.InputKinds));
            builder.Append(" profile=");
            builder.AppendLine(definition.ModelProfile);
        }

        return builder.ToString();
    }
}
