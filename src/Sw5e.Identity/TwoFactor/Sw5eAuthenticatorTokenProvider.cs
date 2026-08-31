using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Sw5e.Identity.TwoFactor;

/// <summary>
/// Validates authenticator-app codes, replacing the framework's provider under
/// the same <see cref="TokenOptions.DefaultAuthenticatorProvider"/> name.
/// </summary>
/// <remarks>
/// <para>
/// Registered over the framework's own provider rather than beside it, so that
/// every route into two-factor verification — <c>VerifyTwoFactorTokenAsync</c>
/// during enrolment, <c>TwoFactorAuthenticatorSignInAsync</c> during sign-in,
/// and anything added later — resolves to this one. A second provider under a
/// different name would be a second, differently-behaved way to satisfy the
/// same check, which is how a tightened window ends up applying to one flow and
/// not the other.
/// </para>
/// <para>
/// What changes relative to the framework's provider is exactly one thing: the
/// acceptance window is read from configuration instead of being a constant
/// compiled into an internal type. Everything else — the secret's storage, the
/// base32 encoding, the algorithm, the step length, the digit count — is
/// unchanged, because those are the parts real authenticator apps depend on.
/// </para>
/// <para>
/// The window matters more than its size suggests. Too wide and a stolen code
/// stays useful for minutes; too narrow and a phone whose clock is twenty
/// seconds fast produces codes the server rejects, which the person holding it
/// experiences as "the authenticator does not work" and never as "my clock is
/// wrong". One step either side is the value nearly every service settles on,
/// and it is what <see cref="Sw5eIdentityOptions.AuthenticatorStepWindow"/>
/// defaults to.
/// </para>
/// </remarks>
public sealed class Sw5eAuthenticatorTokenProvider(
    IOptions<Sw5eIdentityOptions> options,
    TimeProvider timeProvider) : IUserTwoFactorTokenProvider<Sw5eUser>
{
    private readonly int _stepWindow = options.Value.AuthenticatorStepWindow;

    /// <summary>
    /// Nothing. The server does not generate authenticator codes; the app on
    /// the reader's phone does, from a secret they both hold.
    /// </summary>
    /// <remarks>
    /// The framework's contract requires the method to exist. Returning null
    /// matches the framework's own authenticator provider and is what
    /// <c>UserManager.GenerateTwoFactorTokenAsync</c> expects for a provider
    /// that cannot mint tokens server-side.
    /// </remarks>
    public Task<string> GenerateAsync(string purpose, UserManager<Sw5eUser> manager, Sw5eUser user) =>
        Task.FromResult<string>(null!);

    /// <summary>
    /// Whether this account has an authenticator secret to check codes
    /// against.
    /// </summary>
    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<Sw5eUser> manager, Sw5eUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);

        return !string.IsNullOrWhiteSpace(await manager.GetAuthenticatorKeyAsync(user));
    }

    public async Task<bool> ValidateAsync(
        string purpose,
        string token,
        UserManager<Sw5eUser> manager,
        Sw5eUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);

        var key = await manager.GetAuthenticatorKeyAsync(user);

        // No secret means no enrolment, which is not a code that failed to
        // verify but an account that has nothing to verify against. Refusing is
        // the only safe answer either way.
        if (!Rfc6238TimeBasedOneTimePassword.TryDecodeBase32(key, out var secret))
        {
            return false;
        }

        return Rfc6238TimeBasedOneTimePassword.Verify(
            secret,
            token ?? string.Empty,
            _stepWindow,
            timeProvider.GetUtcNow());
    }
}
