﻿using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Client.WebIntegration.OpenIddictClientWebIntegrationConstants;

namespace OpenIddict.Sandbox.AspNetCore.Client.Controllers;

public class AuthenticationController : Controller
{
    private readonly OpenIddictClientService _service;

    public AuthenticationController(OpenIddictClientService service)
        => _service = service;

    [HttpPost("~/login"), ValidateAntiForgeryToken]
    public ActionResult LogIn(string provider, string returnUrl)
    {
        // Note: OpenIddict always validates the specified provider name when handling the challenge operation,
        // but the provider can also be validated earlier to return an error page or a special HTTP error code.
        if (!string.Equals(provider, "Local",          StringComparison.Ordinal) &&
            !string.Equals(provider, "Local+GitHub",   StringComparison.Ordinal) &&
            !string.Equals(provider, Providers.GitHub, StringComparison.Ordinal) &&
            !string.Equals(provider, Providers.Google, StringComparison.Ordinal) &&
            !string.Equals(provider, Providers.Reddit, StringComparison.Ordinal))
        {
            return BadRequest();
        }

        // The local authorization server sample allows the client to select the external
        // identity provider that will be used to eventually authenticate the user. For that,
        // a custom "identity_provider" parameter is sent to the authorization server so that
        // the user is directly redirected to GitHub (in this case, no login page is shown).
        if (string.Equals(provider, "Local+GitHub", StringComparison.Ordinal))
        {
            var properties = new AuthenticationProperties(new Dictionary<string, string>
            {
                // Note: when only one client is registered in the client options,
                // specifying the issuer URI or the provider name is not required.
                [OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = "Local"
            })
            {
                // Only allow local return URLs to prevent open redirect attacks.
                RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/",

                Parameters =
                {
                    [Parameters.IdentityProvider] = "GitHub"
                }
            };

            // Ask the OpenIddict client middleware to redirect the user agent to the identity provider.
            return Challenge(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        }

        else
        {
            var properties = new AuthenticationProperties(new Dictionary<string, string>
            {
                // Note: when only one client is registered in the client options,
                // specifying the issuer URI or the provider name is not required.
                [OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = provider
            })
            {
                // Only allow local return URLs to prevent open redirect attacks.
                RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/"
            };

            // Ask the OpenIddict client middleware to redirect the user agent to the identity provider.
            return Challenge(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        }
    }

    [HttpPost("~/logout"), ValidateAntiForgeryToken]
    public async Task<ActionResult> LogOut(string returnUrl)
    {
        // Retrieve the identity stored in the local authentication cookie. If it's not available,
        // this indicate that the user is already logged out locally (or has not logged in yet).
        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (result is not { Principal.Identity: ClaimsIdentity identity })
        {
            // Only allow local return URLs to prevent open redirect attacks.
            return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
        }

        // Remove the local authentication cookie before triggering a redirection to the remote server.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Extract the client registration identifier and retrieve the associated server configuration.
        // If the provider is known to support remote sign-out, ask OpenIddict to initiate a logout request.
        if (identity.FindFirst(Claims.Private.RegistrationId)?.Value is string identifier &&
            await _service.GetServerConfigurationByRegistrationIdAsync(identifier) is { EndSessionEndpoint: Uri })
        {
            var properties = new AuthenticationProperties(new Dictionary<string, string>
            {
                [OpenIddictClientAspNetCoreConstants.Properties.RegistrationId] = identifier,

                // While not required, the specification encourages sending an id_token_hint
                // parameter containing an identity token returned by the server for this user.
                [OpenIddictClientAspNetCoreConstants.Properties.IdentityTokenHint] =
                    result.Properties.GetTokenValue(OpenIddictClientAspNetCoreConstants.Tokens.BackchannelIdentityToken)
            })
            {
                // Only allow local return URLs to prevent open redirect attacks.
                RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/"
            };

            // Ask the OpenIddict client middleware to redirect the user agent to the identity provider.
            return SignOut(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        }

        // Only allow local return URLs to prevent open redirect attacks.
        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    // Note: this controller uses the same callback action for all providers
    // but for users who prefer using a different action per provider,
    // the following action can be split into separate actions.
    [HttpGet("~/callback/login/{provider}"), HttpPost("~/callback/login/{provider}"), IgnoreAntiforgeryToken]
    public async Task<ActionResult> LogInCallback()
    {
        // Retrieve the authorization data validated by OpenIddict as part of the callback handling.
        var result = await HttpContext.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);

        // Multiple strategies exist to handle OAuth 2.0/OpenID Connect callbacks, each with their pros and cons:
        //
        //   * Directly using the tokens to perform the necessary action(s) on behalf of the user, which is suitable
        //     for applications that don't need a long-term access to the user's resources or don't want to store
        //     access/refresh tokens in a database or in an authentication cookie (which has security implications).
        //     It is also suitable for applications that don't need to authenticate users but only need to perform
        //     action(s) on their behalf by making API calls using the access token returned by the remote server.
        //
        //   * Storing the external claims/tokens in a database (and optionally keeping the essential claims in an
        //     authentication cookie so that cookie size limits are not hit). For the applications that use ASP.NET
        //     Core Identity, the UserManager.SetAuthenticationTokenAsync() API can be used to store external tokens.
        //
        //     Note: in this case, it's recommended to use column encryption to protect the tokens in the database.
        //
        //   * Storing the external claims/tokens in an authentication cookie, which doesn't require having
        //     a user database but may be affected by the cookie size limits enforced by most browser vendors
        //     (e.g Safari for macOS and Safari for iOS/iPadOS enforce a per-domain 4KB limit for all cookies).
        //
        //     Note: this is the approach used here, but the external claims are first filtered to only persist
        //     a few claims like the user identifier. The same approach is used to store the access/refresh tokens.

        // Important: if the remote server doesn't support OpenID Connect and doesn't expose a userinfo endpoint,
        // result.Principal.Identity will represent an unauthenticated identity and won't contain any claim.
        //
        // Such identities cannot be used as-is to build an authentication cookie in ASP.NET Core (as the
        // antiforgery stack requires at least a name claim to bind CSRF cookies to the user's identity) but
        // the access/refresh tokens can be retrieved using result.Properties.GetTokens() to make API calls.
        if (result.Principal is not ClaimsPrincipal { Identity.IsAuthenticated: true })
        {
            throw new InvalidOperationException("The external authorization data cannot be used for authentication.");
        }

        // Build an identity based on the external claims and that will be used to create the authentication cookie.
        //
        // By default, all claims extracted during the authorization dance are available. The claims collection stored
        // in the cookie can be filtered out or mapped to different names depending the claim name or its issuer.
        var claims = new List<Claim>(result.Principal.Claims
            .Select(claim => claim switch
            {
                // Map the standard "sub" and custom "id" claims to ClaimTypes.NameIdentifier, which is
                // the default claim type used by .NET and is required by the antiforgery components.
                { Type: Claims.Subject } or
                { Type: "id", Issuer: "https://github.com/" or "https://twitter.com/" }
                    => new Claim(ClaimTypes.NameIdentifier, claim.Value, claim.ValueType, claim.Issuer),

                // Map the standard "name" claim to ClaimTypes.Name.
                { Type: Claims.Name }
                    => new Claim(ClaimTypes.Name, claim.Value, claim.ValueType, claim.Issuer),

                _ => claim
            })
            .Where(claim => claim switch
            {
                // Preserve the basic claims that are necessary for the application to work correctly.
                { Type: ClaimTypes.NameIdentifier or ClaimTypes.Name } => true,

                // Preserve the client registration identifier as a dedicated claim so that the
                // associated server configuration can be resolved from the logout endpoint to
                // determine whether the authorization server supports client-initiated logouts.
                { Type: Claims.Private.RegistrationId } => true,

                // Applications that use multiple client registrations can filter claims based on the issuer.
                { Type: "bio", Issuer: "https://github.com/" } => true,

                // Don't preserve the other claims.
                _ => false
            }));

        var identity = new ClaimsIdentity(claims,
            authenticationType: CookieAuthenticationDefaults.AuthenticationScheme,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        // Build the authentication properties based on the properties that were added when the challenge was triggered.
        var properties = new AuthenticationProperties(result.Properties.Items)
        {
            RedirectUri = result.Properties.RedirectUri ?? "/"
        };

        // If needed, the tokens returned by the authorization server can be stored in the authentication cookie.
        // To make cookies less heavy, tokens that are not used are filtered out before creating the cookie.
        properties.StoreTokens(result.Properties.GetTokens().Where(token => token switch
        {
            // Preserve the access, identity and refresh tokens returned in the token response, if available.
            {
                Name: OpenIddictClientAspNetCoreConstants.Tokens.BackchannelAccessToken   or
                      OpenIddictClientAspNetCoreConstants.Tokens.BackchannelIdentityToken or
                      OpenIddictClientAspNetCoreConstants.Tokens.RefreshToken
            } => true,

            // Ignore the other tokens.
            _ => false
        }));

        // Ask the cookie authentication handler to return a new cookie and redirect
        // the user agent to the return URL stored in the authentication properties.
        return SignIn(new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    // Note: this controller uses the same callback action for all providers
    // but for users who prefer using a different action per provider,
    // the following action can be split into separate actions.
    [HttpGet("~/callback/logout/{provider}"), HttpPost("~/callback/logout/{provider}"), IgnoreAntiforgeryToken]
    public async Task<ActionResult> LogOutCallback()
    {
        // Retrieve the data stored by OpenIddict in the state token created when the logout was triggered.
        var result = await HttpContext.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);

        // In this sample, the local authentication cookie is always removed before the user agent is redirected
        // to the authorization server. Applications that prefer delaying the removal of the local cookie can
        // remove the corresponding code from the logout action and remove the authentication cookie in this action.

        return Redirect(result!.Properties!.RedirectUri ?? "/");
    }
}
