﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Extensions;
using static OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreConstants;
using JsonWebTokenTypes = OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreConstants.JsonWebTokenTypes;

namespace OpenIddict.Server.AspNetCore;

public static partial class OpenIddictServerAspNetCoreHandlers
{
    public static class Session
    {
        public static ImmutableArray<OpenIddictServerHandlerDescriptor> DefaultHandlers { get; } = ImmutableArray.Create([
            /*
             * End-session request extraction:
             */
            ExtractGetOrPostRequest<ExtractEndSessionRequestContext>.Descriptor,
            RestoreCachedRequestParameters.Descriptor,
            CacheRequestParameters.Descriptor,

            /*
             * End-session request handling:
             */
            EnablePassthroughMode<HandleEndSessionRequestContext, RequireEndSessionEndpointPassthroughEnabled>.Descriptor,

            /*
             * End-session response processing:
             */
            RemoveCachedRequest.Descriptor,
            AttachHttpResponseCode<ApplyEndSessionResponseContext>.Descriptor,
            AttachCacheControlHeader<ApplyEndSessionResponseContext>.Descriptor,
            ProcessHostRedirectionResponse.Descriptor,
            ProcessPassthroughErrorResponse<ApplyEndSessionResponseContext, RequireEndSessionEndpointPassthroughEnabled>.Descriptor,
            ProcessStatusCodePagesErrorResponse<ApplyEndSessionResponseContext>.Descriptor,
            ProcessLocalErrorResponse<ApplyEndSessionResponseContext>.Descriptor,
            ProcessQueryResponse.Descriptor,
            ProcessEmptyResponse<ApplyEndSessionResponseContext>.Descriptor
        ]);

        /// <summary>
        /// Contains the logic responsible for restoring cached requests from the request_id, if specified.
        /// Note: this handler is not used when the OpenID Connect request is not initially handled by ASP.NET Core.
        /// </summary>
        public sealed class RestoreCachedRequestParameters : IOpenIddictServerHandler<ExtractEndSessionRequestContext>
        {
            private readonly IDistributedCache _cache;

            public RestoreCachedRequestParameters() => throw new InvalidOperationException(SR.GetResourceString(SR.ID0116));

            public RestoreCachedRequestParameters(IDistributedCache cache)
                => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

            /// <summary>
            /// Gets the default descriptor definition assigned to this handler.
            /// </summary>
            public static OpenIddictServerHandlerDescriptor Descriptor { get; }
                = OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractEndSessionRequestContext>()
                    .AddFilter<RequireHttpRequest>()
                    .AddFilter<RequireEndSessionRequestCachingEnabled>()
                    .UseSingletonHandler<RestoreCachedRequestParameters>()
                    .SetOrder(ExtractGetOrPostRequest<ExtractEndSessionRequestContext>.Descriptor.Order + 1_000)
                    .SetType(OpenIddictServerHandlerType.BuiltIn)
                    .Build();

            /// <inheritdoc/>
            public async ValueTask HandleAsync(ExtractEndSessionRequestContext context)
            {
                if (context is null)
                {
                    throw new ArgumentNullException(nameof(context));
                }

                Debug.Assert(context.Request is not null, SR.GetResourceString(SR.ID4008));

                // If a request_id parameter can be found in the end session request,
                // restore the complete end session request from the distributed cache.

                if (string.IsNullOrEmpty(context.Request.RequestId))
                {
                    return;
                }

                // Note: the cache key is always prefixed with a specific marker
                // to avoid collisions with the other types of cached payloads.
                var token = await _cache.GetStringAsync(Cache.EndSessionRequest + context.Request.RequestId);
                if (token is null || !context.Options.JsonWebTokenHandler.CanReadToken(token))
                {
                    context.Logger.LogInformation(SR.GetResourceString(SR.ID6150), Parameters.RequestId);

                    context.Reject(
                        error: Errors.InvalidRequest,
                        description: SR.FormatID2052(Parameters.RequestId),
                        uri: SR.FormatID8000(SR.ID2052));

                    return;
                }

                var parameters = context.Options.TokenValidationParameters.Clone();
                parameters.ValidIssuer ??= (context.Options.Issuer ?? context.BaseUri)?.AbsoluteUri;
                parameters.ValidAudience ??= parameters.ValidIssuer;
                parameters.ValidTypes = [JsonWebTokenTypes.Private.EndSessionRequest];

                var result = await context.Options.JsonWebTokenHandler.ValidateTokenAsync(token, parameters);
                if (!result.IsValid)
                {
                    context.Logger.LogInformation(SR.GetResourceString(SR.ID6150), Parameters.RequestId);

                    context.Reject(
                        error: Errors.InvalidRequest,
                        description: SR.FormatID2052(Parameters.RequestId),
                        uri: SR.FormatID8000(SR.ID2052));

                    return;
                }

                using var document = JsonDocument.Parse(
                    Base64UrlEncoder.Decode(((JsonWebToken) result.SecurityToken).InnerToken.EncodedPayload));
                if (document.RootElement.ValueKind is not JsonValueKind.Object)
                {
                    throw new InvalidOperationException(SR.GetResourceString(SR.ID0118));
                }

                // Restore the request parameters from the serialized payload.
                foreach (var parameter in document.RootElement.EnumerateObject())
                {
                    if (!context.Request.HasParameter(parameter.Name))
                    {
                        context.Request.AddParameter(parameter.Name, parameter.Value.Clone());
                    }
                }
            }
        }

        /// <summary>
        /// Contains the logic responsible for caching end session requests, if applicable.
        /// Note: this handler is not used when the OpenID Connect request is not initially handled by ASP.NET Core.
        /// </summary>
        public sealed class CacheRequestParameters : IOpenIddictServerHandler<ExtractEndSessionRequestContext>
        {
            private readonly IDistributedCache _cache;
            private readonly IOptionsMonitor<OpenIddictServerAspNetCoreOptions> _options;

            public CacheRequestParameters() => throw new InvalidOperationException(SR.GetResourceString(SR.ID0116));

            public CacheRequestParameters(
                IDistributedCache cache,
                IOptionsMonitor<OpenIddictServerAspNetCoreOptions> options)
            {
                _cache = cache ?? throw new ArgumentNullException(nameof(cache));
                _options = options ?? throw new ArgumentNullException(nameof(options));
            }

            /// <summary>
            /// Gets the default descriptor definition assigned to this handler.
            /// </summary>
            public static OpenIddictServerHandlerDescriptor Descriptor { get; }
                = OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractEndSessionRequestContext>()
                    .AddFilter<RequireHttpRequest>()
                    .AddFilter<RequireEndSessionRequestCachingEnabled>()
                    .UseSingletonHandler<CacheRequestParameters>()
                    .SetOrder(RestoreCachedRequestParameters.Descriptor.Order + 1_000)
                    .SetType(OpenIddictServerHandlerType.BuiltIn)
                    .Build();

            /// <inheritdoc/>
            public async ValueTask HandleAsync(ExtractEndSessionRequestContext context)
            {
                if (context is null)
                {
                    throw new ArgumentNullException(nameof(context));
                }

                if (context is not { BaseUri.IsAbsoluteUri: true, RequestUri.IsAbsoluteUri: true })
                {
                    throw new InvalidOperationException(SR.GetResourceString(SR.ID0127));
                }

                Debug.Assert(context.Request is not null, SR.GetResourceString(SR.ID4008));

                // This handler only applies to ASP.NET Core requests. If the HTTP context cannot be resolved,
                // this may indicate that the request was incorrectly processed by another server stack.
                var request = context.Transaction.GetHttpRequest() ??
                    throw new InvalidOperationException(SR.GetResourceString(SR.ID0114));

                // Don't cache the request if the request doesn't include any parameter.
                // If a request_id parameter can be found in the end session request,
                // ignore the following logic to prevent an infinite redirect loop.
                if (context.Request.Count is 0 || !string.IsNullOrEmpty(context.Request.RequestId))
                {
                    return;
                }

                // Generate a 256-bit request identifier using a crypto-secure random number generator.
                context.Request.RequestId = Base64UrlEncoder.Encode(OpenIddictHelpers.CreateRandomArray(size: 256));

                // Build a list of claims matching the parameters extracted from the request.
                //
                // Note: in most cases, parameters should be representated as strings as requests are
                // typically resolved from the query string or the request form, where parameters
                // are natively represented as strings. However, requests can also be extracted from
                // different places where they can be represented as complex JSON representations
                // (e.g requests extracted from a JSON Web Token that may be encrypted and/or signed).
                var claims = from parameter in context.Request.GetParameters()
                             let element = (JsonElement) parameter.Value
                             let type = element.ValueKind switch
                             {
                                 JsonValueKind.String                          => ClaimValueTypes.String,
                                 JsonValueKind.Number                          => ClaimValueTypes.Integer64,
                                 JsonValueKind.True or JsonValueKind.False     => ClaimValueTypes.Boolean,
                                 JsonValueKind.Null or JsonValueKind.Undefined => JsonClaimValueTypes.JsonNull,
                                 JsonValueKind.Array                           => JsonClaimValueTypes.JsonArray,
                                 JsonValueKind.Object or _                     => JsonClaimValueTypes.Json
                             }
                             select new Claim(parameter.Key, element.ToString()!, type);

                // Store the serialized end session request parameters in the distributed cache.
                var token = context.Options.JsonWebTokenHandler.CreateToken(new SecurityTokenDescriptor
                {
                    Audience = (context.Options.Issuer ?? context.BaseUri)?.AbsoluteUri,
                    EncryptingCredentials = context.Options.EncryptionCredentials.First(),
                    Issuer = (context.Options.Issuer ?? context.BaseUri)?.AbsoluteUri,
                    SigningCredentials = context.Options.SigningCredentials.First(),
                    Subject = new ClaimsIdentity(claims, TokenValidationParameters.DefaultAuthenticationType),
                    TokenType = JsonWebTokenTypes.Private.EndSessionRequest
                });

                // Note: the cache key is always prefixed with a specific marker
                // to avoid collisions with the other types of cached payloads.
                await _cache.SetStringAsync(Cache.EndSessionRequest + context.Request.RequestId,
                    token, _options.CurrentValue.EndSessionRequestCachingPolicy);

                // Create a new GET end session request containing only the request_id parameter.
                var location = QueryHelpers.AddQueryString(
                    uri: new UriBuilder(context.RequestUri) { Query = null }.Uri.AbsoluteUri,
                    name: Parameters.RequestId,
                    value: context.Request.RequestId);

                request.HttpContext.Response.Redirect(location);

                // Mark the response as handled to skip the rest of the pipeline.
                context.HandleRequest();
            }
        }

        /// <summary>
        /// Contains the logic responsible for removing cached end session requests from the distributed cache.
        /// Note: this handler is not used when the OpenID Connect request is not initially handled by ASP.NET Core.
        /// </summary>
        public sealed class RemoveCachedRequest : IOpenIddictServerHandler<ApplyEndSessionResponseContext>
        {
            private readonly IDistributedCache _cache;

            public RemoveCachedRequest() => throw new InvalidOperationException(SR.GetResourceString(SR.ID0116));

            public RemoveCachedRequest(IDistributedCache cache)
                => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

            /// <summary>
            /// Gets the default descriptor definition assigned to this handler.
            /// </summary>
            public static OpenIddictServerHandlerDescriptor Descriptor { get; }
                = OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyEndSessionResponseContext>()
                    .AddFilter<RequireHttpRequest>()
                    .AddFilter<RequireEndSessionRequestCachingEnabled>()
                    .UseSingletonHandler<RemoveCachedRequest>()
                    .SetOrder(int.MinValue + 100_000)
                    .SetType(OpenIddictServerHandlerType.BuiltIn)
                    .Build();

            /// <inheritdoc/>
            public ValueTask HandleAsync(ApplyEndSessionResponseContext context)
            {
                if (context is null)
                {
                    throw new ArgumentNullException(nameof(context));
                }

                if (string.IsNullOrEmpty(context.Request?.RequestId))
                {
                    return default;
                }

                // Note: the ApplyEndSessionResponse event is called for both successful
                // and errored end session responses but discrimination is not necessary here,
                // as the end session request must be removed from the distributed cache in both cases.

                // Note: the cache key is always prefixed with a specific marker
                // to avoid collisions with the other types of cached payloads.
                return new(_cache.RemoveAsync(Cache.EndSessionRequest + context.Request.RequestId));
            }
        }

        /// <summary>
        /// Contains the logic responsible for processing end session responses.
        /// Note: this handler is not used when the OpenID Connect request is not initially handled by ASP.NET Core.
        /// </summary>
        public sealed class ProcessQueryResponse : IOpenIddictServerHandler<ApplyEndSessionResponseContext>
        {
            /// <summary>
            /// Gets the default descriptor definition assigned to this handler.
            /// </summary>
            public static OpenIddictServerHandlerDescriptor Descriptor { get; }
                = OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyEndSessionResponseContext>()
                    .AddFilter<RequireHttpRequest>()
                    .UseSingletonHandler<ProcessQueryResponse>()
                    .SetOrder(250_000)
                    .SetType(OpenIddictServerHandlerType.BuiltIn)
                    .Build();

            /// <inheritdoc/>
            public ValueTask HandleAsync(ApplyEndSessionResponseContext context)
            {
                if (context is null)
                {
                    throw new ArgumentNullException(nameof(context));
                }

                // This handler only applies to ASP.NET Core requests. If the HTTP context cannot be resolved,
                // this may indicate that the request was incorrectly processed by another server stack.
                var response = context.Transaction.GetHttpRequest()?.HttpContext.Response ??
                    throw new InvalidOperationException(SR.GetResourceString(SR.ID0114));

                if (string.IsNullOrEmpty(context.PostLogoutRedirectUri))
                {
                    return default;
                }

                context.Logger.LogInformation(SR.GetResourceString(SR.ID6151), context.PostLogoutRedirectUri, context.Response);

                // Note: while initially not allowed by the core OAuth 2.0 specification, multiple parameters
                // with the same name are used by derived drafts like the OAuth 2.0 token exchange specification.
                // For consistency, multiple parameters with the same name are also supported by this endpoint.

#if SUPPORTS_MULTIPLE_VALUES_IN_QUERYHELPERS
                var location = QueryHelpers.AddQueryString(context.PostLogoutRedirectUri,
                    from parameter in context.Response.GetParameters()
                    let values = (string?[]?) parameter.Value
                    where values is not null
                    from value in values
                    where !string.IsNullOrEmpty(value)
                    select KeyValuePair.Create(parameter.Key, value));
#else
                var location = context.PostLogoutRedirectUri;

                foreach (var (key, value) in
                    from parameter in context.Response.GetParameters()
                    let values = (string?[]?) parameter.Value
                    where values is not null
                    from value in values
                    where !string.IsNullOrEmpty(value)
                    select (parameter.Key, Value: value))
                {
                    location = QueryHelpers.AddQueryString(location, key, value);
                }
#endif
                response.Redirect(location);
                context.HandleRequest();

                return default;
            }
        }

        /// <summary>
        /// Contains the logic responsible for processing end session responses that should trigger a host redirection.
        /// Note: this handler is not used when the OpenID Connect request is not initially handled by ASP.NET Core.
        /// </summary>
        public sealed class ProcessHostRedirectionResponse : IOpenIddictServerHandler<ApplyEndSessionResponseContext>
        {
            /// <summary>
            /// Gets the default descriptor definition assigned to this handler.
            /// </summary>
            public static OpenIddictServerHandlerDescriptor Descriptor { get; }
                = OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyEndSessionResponseContext>()
                    .AddFilter<RequireHttpRequest>()
                    .UseSingletonHandler<ProcessHostRedirectionResponse>()
                    .SetOrder(ProcessPassthroughErrorResponse<ApplyEndSessionResponseContext, RequireEndSessionEndpointPassthroughEnabled>.Descriptor.Order + 250)
                    .SetType(OpenIddictServerHandlerType.BuiltIn)
                    .Build();

            /// <inheritdoc/>
            public ValueTask HandleAsync(ApplyEndSessionResponseContext context)
            {
                if (context is null)
                {
                    throw new ArgumentNullException(nameof(context));
                }

                // This handler only applies to ASP.NET Core requests. If the HTTP context cannot be resolved,
                // this may indicate that the request was incorrectly processed by another server stack.
                var response = context.Transaction.GetHttpRequest()?.HttpContext.Response ??
                    throw new InvalidOperationException(SR.GetResourceString(SR.ID0114));

                // Note: this handler only executes if no post_logout_redirect_uri was specified
                // and if the response doesn't correspond to an error, that must be handled locally.
                if (!string.IsNullOrEmpty(context.PostLogoutRedirectUri) ||
                    !string.IsNullOrEmpty(context.Response.Error))
                {
                    return default;
                }

                var properties = context.Transaction.GetProperty<AuthenticationProperties>(typeof(AuthenticationProperties).FullName!);
                if (properties is not null && !string.IsNullOrEmpty(properties.RedirectUri))
                {
                    response.Redirect(properties.RedirectUri);

                    context.Logger.LogInformation(SR.GetResourceString(SR.ID6144));
                    context.HandleRequest();
                }

                return default;
            }
        }
    }
}
