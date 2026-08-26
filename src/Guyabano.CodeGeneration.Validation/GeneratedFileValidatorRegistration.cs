namespace Guyabano.CodeGeneration.Validation;

internal sealed record GeneratedFileValidatorRegistration(
    string Extension,
    IGeneratedFileValidator Validator);
