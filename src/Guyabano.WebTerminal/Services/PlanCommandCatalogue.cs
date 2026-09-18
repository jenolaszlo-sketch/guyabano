namespace Guyabano.WebTerminal.Services;

/// <summary>The production authoring catalogue plus its rendered summary.</summary>
public sealed record PlanCommandCatalogue(
    Penghou.Fuwen.Compiler.ITrustedCatalogue Catalogue,
    string Summary);
