using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Security;

/// <summary>Registers an owner-supplied OAuth scope as an authorization policy without owning the scope name.</summary>
public static class ScopeAuthorizationExtensions
{
    public static AuthorizationBuilder AddRequiredScopePolicy(
        this AuthorizationBuilder builder,
        string policyName,
        string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(policyName))
        {
            throw new ArgumentException("Policy name is required.", nameof(policyName));
        }

        if (string.IsNullOrWhiteSpace(requiredScope))
        {
            throw new ArgumentException("Required scope is required.", nameof(requiredScope));
        }

        return builder.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => HasScope(context.User.FindFirst("scope")?.Value, requiredScope));
        });
    }

    private static bool HasScope(string? claim, string expected) =>
        claim?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expected, StringComparer.Ordinal) == true;
}
