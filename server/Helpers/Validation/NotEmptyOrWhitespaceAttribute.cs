using System.ComponentModel.DataAnnotations;

namespace server.Helpers.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NotEmptyOrWhitespaceAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s);
    }
}

