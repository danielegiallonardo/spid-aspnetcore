﻿using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SPID.AspNetCore.Authentication.Events;
using SPID.AspNetCore.Authentication.Helpers;
using SPID.AspNetCore.Authentication.Models;
using SPID.AspNetCore.Authentication.Models.IdP;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Serialization;

namespace SPID.AspNetCore.Authentication
{
    public class SpidHandler : RemoteAuthenticationHandler<SpidOptions>, IAuthenticationSignOutHandler
    {
        private const string CorrelationProperty = ".xsrf";
        EventsHandler _eventsHandler;
        RequestGenerator _requestGenerator;

        public SpidHandler(IOptionsMonitor<SpidOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
            : base(options, logger, encoder, clock)
        {
        }

        protected new SpidEvents Events
        {
            get { return (SpidEvents)base.Events; }
            set { base.Events = value; }
        }

        protected override Task<object> CreateEventsAsync() => Task.FromResult<object>(new SpidEvents());

        /// <summary>
        /// Decides whether this handler should handle request based on request path. If it's true, HandleRequestAsync method is invoked.
        /// </summary>
        /// <returns>value indicating whether the request should be handled or not</returns>
        public override async Task<bool> ShouldHandleRequestAsync()
        {
            var result = await base.ShouldHandleRequestAsync();
            if (!result)
            {
                result = Options.RemoteSignOutPath == Request.Path;
            }
            return result;
        }

        /// <summary>
        /// Handle the request and de
        /// </summary>
        /// <returns></returns>
        public override Task<bool> HandleRequestAsync()
        {
            _eventsHandler = new EventsHandler(Events);
            _requestGenerator = new RequestGenerator(Response, Logger);

            // RemoteSignOutPath and CallbackPath may be the same, fall through if the message doesn't match.
            if (Options.RemoteSignOutPath.HasValue && Options.RemoteSignOutPath == Request.Path)
            {
                // We've received a remote sign-out request
                return HandleRemoteSignOutAsync();
            }

            return base.HandleRequestAsync();
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            // Save the original challenge URI so we can redirect back to it when we're done.
            if (string.IsNullOrEmpty(properties.RedirectUri))
            {
                properties.RedirectUri = OriginalPathBase + OriginalPath + Request.QueryString;
            }

            // Create the SPID request id
            string authenticationRequestId = Guid.NewGuid().ToString();

            // Select the Identity Provider
            var idpName = Request.Query["idpName"];
            var idp = Options.IdentityProviders.FirstOrDefault(x => x.Name == idpName);


            var securityTokenCreatingContext = await _eventsHandler.HandleSecurityTokenCreatingContext(Context, Scheme, Options, properties, authenticationRequestId);

            // Create the signed SAML request
            var message = SamlHelper.BuildAuthnPostRequest(
                authenticationRequestId,
                securityTokenCreatingContext.TokenOptions.EntityId,
                securityTokenCreatingContext.TokenOptions.AssertionConsumerServiceIndex,
                securityTokenCreatingContext.TokenOptions.AttributeConsumingServiceIndex,
                2,
                securityTokenCreatingContext.TokenOptions.Certificate,
                idp);

            GenerateCorrelationId(properties);

            var (redirectHandled, afterRedirectMessage) = await _eventsHandler.HandleRedirectToIdentityProviderForAuthentication(Context, Scheme, Options, properties, message);
            if (redirectHandled)
            {
                return;
            }
            message = afterRedirectMessage;

            properties.SetIdentityProviderName(idpName);
            properties.SetAuthenticationRequest(message);
            properties.Save(Response, Options.StateDataFormat);

            await _requestGenerator.HandleAuthenticationRequest(message, securityTokenCreatingContext.TokenOptions.Certificate, idp.SingleSignOnServiceUrl, idp.Method);
        }

        protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
        {
            AuthenticationProperties properties = new AuthenticationProperties();
            properties.Load(Request, Options.StateDataFormat);

            var (id, message) = await ExtractInfoFromAuthenticationResponse();

            var validationMessageResult = ValidateAuthenticationResponse(message, properties);
            if (validationMessageResult != null)
                return validationMessageResult;

            try
            {
                var idpName = properties.GetIdentityProviderName();
                var request = properties.GetAuthenticationRequest();

                var responseMessageReceivedResult = await _eventsHandler.HandleAuthenticationResponseMessageReceived(Context, Scheme, Options, properties, message);
                if (responseMessageReceivedResult.Result != null)
                {
                    return responseMessageReceivedResult.Result;
                }
                message = responseMessageReceivedResult.ProtocolMessage;
                properties = responseMessageReceivedResult.Properties;

                var correlationValidationResult = ValidateCorrelation(properties);
                if (correlationValidationResult != null)
                {
                    return correlationValidationResult;
                }

                var (principal, validFrom, validTo) = CreatePrincipal(message, request, idpName);

                AdjustAuthenticationPropertiesDates(properties, validFrom, validTo);

                properties.SetSubjectNameId(message.Assertion.Subject?.NameID?.Text);
                properties.SetSessionIndex(message.Assertion.AuthnStatement.SessionIndex);
                properties.Save(Response, Options.StateDataFormat);

                var ticket = new AuthenticationTicket(principal, properties, Scheme.Name);
                await _eventsHandler.HandleAuthenticationSuccess(Context, Scheme, Options, id, ticket);
                return HandleRequestResult.Success(ticket);
            }
            catch (Exception exception)
            {
                Logger.ExceptionProcessingMessage(exception);

                var authenticationFailedResult = await _eventsHandler.HandleAuthenticationFailed(Context, Scheme, Options, message, exception);
                return authenticationFailedResult.Result ?? HandleRequestResult.Fail(exception, properties);
            }
        }

        public async virtual Task SignOutAsync(AuthenticationProperties properties)
        {
            var target = ResolveTarget(Options.ForwardSignOut);
            if (target != null)
            {
                await Context.SignOutAsync(target, properties);
                return;
            }

            string authenticationRequestId = Guid.NewGuid().ToString();

            var requestProperties = new AuthenticationProperties();
            requestProperties.Load(Request, Options.StateDataFormat);

            // Extract the user state from properties and reset.
            var idpName = requestProperties.GetIdentityProviderName();
            var subjectNameId = requestProperties.GetSubjectNameId();
            var sessionIndex = requestProperties.GetSessionIndex();

            var idp = Options.IdentityProviders.FirstOrDefault(i => i.Name == idpName);

            var securityTokenCreatingContext = await _eventsHandler.HandleSecurityTokenCreatingContext(Context, Scheme, Options, properties, authenticationRequestId);

            var message = SamlHelper.BuildLogoutPostRequest(
                authenticationRequestId,
                securityTokenCreatingContext.Options.EntityId,
                securityTokenCreatingContext.Options.Certificate,
                idp,
                subjectNameId,
                sessionIndex);

            var (redirectHandled, afterRedirectMessage) = await _eventsHandler.HandleRedirectToIdentityProviderForSignOut(Context, Scheme, Options, properties, message);
            if (redirectHandled)
            {
                return;
            }
            message = afterRedirectMessage;

            properties.SetLogoutRequest(message);
            properties.Save(Response, Options.StateDataFormat);

            await _requestGenerator.HandleLogoutRequest(message, Options.Certificate, idp.SingleSignOutServiceUrl, idp.Method);
        }

        protected virtual async Task<bool> HandleRemoteSignOutAsync()
        {
            var (id, message) = await ExtractInfoFromSignOutResponse();

            AuthenticationProperties requestProperties = new AuthenticationProperties();
            requestProperties.Load(Request, Options.StateDataFormat);

            var logoutRequest = requestProperties.GetLogoutRequest();

            var validSignOut = ValidateSignOutResponse(message, logoutRequest);
            if (!validSignOut)
                return false;

            var remoteSignOutContext = await _eventsHandler.HandleRemoteSignOut(Context, Scheme, Options, message);
            if (remoteSignOutContext.Result != null)
            {
                if (remoteSignOutContext.Result.Handled)
                {
                    Logger.RemoteSignOutHandledResponse();
                    return true;
                }
                if (remoteSignOutContext.Result.Skipped)
                {
                    Logger.RemoteSignOutSkipped();
                    return false;
                }
            }

            Logger.RemoteSignOut();

            await Context.SignOutAsync(Options.SignOutScheme);
            Response.Redirect(requestProperties.RedirectUri);
            return true;
        }

        private HandleRequestResult ValidateAuthenticationResponse(Response message, AuthenticationProperties properties)
        {
            if (message == null)
            {
                if (Options.SkipUnrecognizedRequests)
                {
                    return HandleRequestResult.SkipHandler();
                }

                return HandleRequestResult.Fail("No message.");
            }

            if (properties == null && !Options.AllowUnsolicitedLogins)
            {
                return HandleRequestResult.Fail("Unsolicited logins are not allowed.");
            }

            return null;
        }

        private HandleRequestResult ValidateCorrelation(AuthenticationProperties properties)
        {
            if (properties.GetCorrelationProperty() != null && !ValidateCorrelationId(properties))
            {
                return HandleRequestResult.Fail("Correlation failed.", properties);
            }
            return null;
        }

        private void AdjustAuthenticationPropertiesDates(AuthenticationProperties properties, DateTimeOffset? validFrom, DateTimeOffset? validTo)
        {
            if (Options.UseTokenLifetime && validFrom != null && validTo != null)
            {
                // Override any session persistence to match the token lifetime.
                var issued = validFrom;
                if (issued != DateTimeOffset.MinValue)
                {
                    properties.IssuedUtc = issued.Value.ToUniversalTime();
                }
                var expires = validTo;
                if (expires != DateTimeOffset.MinValue)
                {
                    properties.ExpiresUtc = expires.Value.ToUniversalTime();
                }
                properties.AllowRefresh = false;
            }
        }

        private (ClaimsPrincipal principal, DateTimeOffset? validFrom, DateTimeOffset? validTo) CreatePrincipal(Response idpAuthnResponse, AuthnRequestType request, string idPName)
        {
            var idp = Options.IdentityProviders.FirstOrDefault(x => x.Name == idPName);

            EntityDescriptor metadataIdp = !string.IsNullOrWhiteSpace(idp.OrganizationUrlMetadata)
                ? idp.OrganizationUrlMetadata.DownloadMetadataIDP()
                : new EntityDescriptor();
            idpAuthnResponse.ValidateAuthnResponse(request, metadataIdp, idp.PerformFullResponseValidation);

            var claims = new Claim[]
            {
                new Claim( ClaimTypes.NameIdentifier, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.email.Equals(x.Name) || SamlConst.email.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( ClaimTypes.Email, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.email.Equals(x.Name) || SamlConst.email.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.name, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.name.Equals(x.Name) || SamlConst.name.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.email, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.email.Equals(x.Name) || SamlConst.email.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.familyName, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.familyName.Equals(x.Name) || SamlConst.familyName.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.fiscalNumber, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.fiscalNumber.Equals(x.Name) || SamlConst.fiscalNumber.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.surname, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.surname.Equals(x.Name) || SamlConst.surname.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.mail, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.mail.Equals(x.Name) || SamlConst.mail.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.address, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.address.Equals(x.Name) || SamlConst.address.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.companyName, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.companyName.Equals(x.Name) || SamlConst.companyName.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.countyOfBirth, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.countyOfBirth.Equals(x.Name) || SamlConst.countyOfBirth.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.dateOfBirth, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.dateOfBirth.Equals(x.Name) || SamlConst.dateOfBirth.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.digitalAddress, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.digitalAddress.Equals(x.Name) || SamlConst.digitalAddress.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.expirationDate, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.expirationDate.Equals(x.Name) || SamlConst.expirationDate.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.gender, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.gender.Equals(x.Name) || SamlConst.gender.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.idCard, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.idCard.Equals(x.Name) || SamlConst.idCard.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.ivaCode, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.ivaCode.Equals(x.Name) || SamlConst.ivaCode.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.mobilePhone, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.mobilePhone.Equals(x.Name) || SamlConst.mobilePhone.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.placeOfBirth, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.placeOfBirth.Equals(x.Name) || SamlConst.placeOfBirth.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.registeredOffice, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.registeredOffice.Equals(x.Name) || SamlConst.registeredOffice.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
                new Claim( SamlConst.spidCode, idpAuthnResponse.Assertion.AttributeStatement.Attribute.FirstOrDefault(x => SamlConst.spidCode.Equals(x.Name) || SamlConst.spidCode.Equals(x.FriendlyName))?.AttributeValue?.Trim() ?? string.Empty),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name, SamlConst.email, null);

            var returnedPrincipal = new ClaimsPrincipal(identity);
            return (returnedPrincipal, DateTimeOffset.Parse(idpAuthnResponse.IssueInstant), DateTimeOffset.Parse(idpAuthnResponse.Assertion.Subject.SubjectConfirmation.SubjectConfirmationData.NotOnOrAfter));
        }

        private async Task<(string Id, Response Message)> ExtractInfoFromAuthenticationResponse()
        {
            if (HttpMethods.IsPost(Request.Method)
              && !string.IsNullOrEmpty(Request.ContentType)
              // May have media/type; charset=utf-8, allow partial match.
              && Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              && Request.Body.CanRead)
            {
                var form = await Request.ReadFormAsync();

                return (
                    form["RelayState"].ToString(),
                    SamlHelper.GetAuthnResponse(form["SAMLResponse"][0])
                );
            }
            return (null, null);
        }

        private async Task<(string Id, IdpLogoutResponse Message)> ExtractInfoFromSignOutResponse()
        {
            if (HttpMethods.IsPost(Request.Method)
              && !string.IsNullOrEmpty(Request.ContentType)
              && Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              && Request.Body.CanRead)
            {
                var form = await Request.ReadFormAsync();

                return (
                    form["RelayState"].ToString(),
                    SamlHelper.GetLogoutResponse(form["SAMLResponse"][0])
                );
            }
            return (null, null);
        }

        private bool ValidateSignOutResponse(IdpLogoutResponse message, LogoutRequestType logoutRequest)
        {
            var valid = message.IsSuccessful && SamlHelper.ValidateLogoutResponse(message, logoutRequest);
            if (valid)
            {
                return true;
            }

            Logger.RemoteSignOutFailed();
            return false;
        }

        private class EventsHandler
        {
            private SpidEvents _events;

            public EventsHandler(SpidEvents events)
            {
                _events = events;
            }

            public async Task<SecurityTokenCreatingContext> HandleSecurityTokenCreatingContext(HttpContext context, AuthenticationScheme scheme, SpidOptions options, AuthenticationProperties properties, string samlAuthnRequestId)
            {
                var securityTokenCreatingContext = new SecurityTokenCreatingContext(context, scheme, options, properties)
                {
                    SamlAuthnRequestId = samlAuthnRequestId,
                    TokenOptions = new SecurityTokenCreatingOptions
                    {
                        EntityId = options.EntityId,
                        Certificate = options.Certificate,
                        AssertionConsumerServiceIndex = options.AssertionConsumerServiceIndex,
                        AttributeConsumingServiceIndex = options.AttributeConsumingServiceIndex
                    }
                };
                await _events.TokenCreating(securityTokenCreatingContext);
                return securityTokenCreatingContext;
            }

            public async Task<(bool, AuthnRequestType)> HandleRedirectToIdentityProviderForAuthentication(HttpContext context, AuthenticationScheme scheme, SpidOptions options, AuthenticationProperties properties, AuthnRequestType message)
            {
                var redirectContext = new RedirectContext(context, scheme, options, properties, message);
                await _events.RedirectToIdentityProvider(redirectContext);
                return (redirectContext.Handled, (AuthnRequestType)redirectContext.SignedProtocolMessage);
            }

            public async Task<(bool, LogoutRequestType)> HandleRedirectToIdentityProviderForSignOut(HttpContext context, AuthenticationScheme scheme, SpidOptions options, AuthenticationProperties properties, LogoutRequestType message)
            {
                var redirectContext = new RedirectContext(context, scheme, options, properties, message);
                await _events.RedirectToIdentityProvider(redirectContext);
                return (redirectContext.Handled, (LogoutRequestType)redirectContext.SignedProtocolMessage);
            }

            public async Task<MessageReceivedContext> HandleAuthenticationResponseMessageReceived(HttpContext context, AuthenticationScheme scheme, SpidOptions options, AuthenticationProperties properties, Response message)
            {
                var messageReceivedContext = new MessageReceivedContext(context, scheme, options, properties, message);
                await _events.MessageReceived(messageReceivedContext);
                return messageReceivedContext;
            }

            public async Task<AuthenticationSuccessContext> HandleAuthenticationSuccess(HttpContext context, AuthenticationScheme scheme, SpidOptions options, string authenticationRequestId, AuthenticationTicket ticket)
            {
                var authenticationSuccessContext = new AuthenticationSuccessContext(context, scheme, options, authenticationRequestId, ticket);
                await _events.AuthenticationSuccess(authenticationSuccessContext);
                return authenticationSuccessContext;
            }

            public async Task<AuthenticationFailedContext> HandleAuthenticationFailed(HttpContext context, AuthenticationScheme scheme, SpidOptions options, Response message, Exception exception)
            {
                var authenticationFailedContext = new AuthenticationFailedContext(context, scheme, options, message, exception);
                await _events.AuthenticationFailed(authenticationFailedContext);
                return authenticationFailedContext;
            }

            public async Task<RemoteSignOutContext> HandleRemoteSignOut(HttpContext context, AuthenticationScheme scheme, SpidOptions options, IdpLogoutResponse message)
            {
                var remoteSignOutContext = new RemoteSignOutContext(context, scheme, options, message);
                await _events.RemoteSignOut(remoteSignOutContext);
                return remoteSignOutContext;
            }
        }

        private class RequestGenerator
        {
            HttpResponse _response;
            ILogger _logger;

            public RequestGenerator(HttpResponse response, ILogger logger)
            {
                _response = response;
                _logger = logger;
            }

            public async Task HandleAuthenticationRequest(AuthnRequestType message, X509Certificate2 certificate, string signOnUrl, RequestMethod method)
            {
                var messageGuid = message.ID.Replace("_", String.Empty);

                if (method == RequestMethod.Post)
                {
                    await _response.WriteAsync($"<html><head><title>Login</title></head><body><form id=\"spidform\" action=\"{signOnUrl}\" method=\"post\">" +
                          $"<input type=\"hidden\" name=\"SAMLRequest\" value=\"{SamlHelper.SignRequest(message, certificate, message.ID)}\" />" +
                          $"<input type=\"hidden\" name=\"RelayState\" value=\"{messageGuid}\" />" +
                          $"<button id=\"btnLogin\">Login</button>" +
                          "<script>document.getElementById('btnLogin').click()</script>" +
                          "</form></body></html>");
                }
                else
                {
                    string redirectUri = GetRedirectUrl(signOnUrl, messageGuid, SamlHelper.SerializeMessage(message), certificate);
                    if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                    {
                        _logger.MalformedRedirectUri(redirectUri);
                    }
                    _response.Redirect(redirectUri);
                }
            }

            public async Task HandleLogoutRequest(LogoutRequestType message, X509Certificate2 certificate, string signOutUrl, RequestMethod method)
            {
                var messageGuid = message.ID.Replace("_", String.Empty);

                if (method == RequestMethod.Post)
                {
                    await _response.WriteAsync($"<html><head><title>Login</title></head><body><form id=\"spidform\" action=\"{signOutUrl}\" method=\"post\">" +
                          $"<input type=\"hidden\" name=\"SAMLRequest\" value=\"{SamlHelper.SignRequest(message, certificate, message.ID)}\" />" + //signed
                          $"<input type=\"hidden\" name=\"RelayState\" value=\"{messageGuid}\" />" + //samlAuthnRequestId
                          $"<button id=\"btnLogout\">Logout</button>" +
                          "<script>document.getElementById('btnLogout').click()</script>" +
                          "</form></body></html>");
                }
                else
                {
                    var redirectUri = GetRedirectUrl(signOutUrl, messageGuid, "", /*signed, */certificate);
                    if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                    {
                        _logger.MalformedRedirectUri(redirectUri);
                    }
                    _response.Redirect(redirectUri);
                }
            }

            private string GetRedirectUrl(string signOnSignOutUrl, string samlAuthnRequestId, string data, X509Certificate2 certificate)
            {
                var samlEndpoint = signOnSignOutUrl;

                var queryStringSeparator = samlEndpoint.Contains("?") ? "&" : "?";

                var dict = new Dictionary<string, StringValues>()
                {
                    { "SAMLRequest", DeflateString(data) },
                    { "RelayState", samlAuthnRequestId },
                    { "SigAlg", SamlConst.SignatureMethod}
                };

                var queryStringNoSignature = BuildURLParametersString(dict).Substring(1);

                var signatureQuery = queryStringNoSignature.CreateSignature(certificate);

                dict.Add("Signature", signatureQuery);

                return samlEndpoint + queryStringSeparator + BuildURLParametersString(dict).Substring(1);
            }

            private string DeflateString(string value)
            {
                using MemoryStream output = new MemoryStream();
                using DeflateStream gzip = new DeflateStream(output, CompressionMode.Compress);
                using StreamWriter writer = new StreamWriter(gzip, Encoding.UTF8);
                writer.Write(value);

                return Convert.ToBase64String(output.ToArray());
            }

            private string BuildURLParametersString(Dictionary<string, StringValues> parameters)
            {
                UriBuilder uriBuilder = new UriBuilder();
                var query = HttpUtility.ParseQueryString(uriBuilder.Query);
                foreach (var urlParameter in parameters)
                {
                    query[urlParameter.Key] = urlParameter.Value;
                }
                uriBuilder.Query = query.ToString();
                return uriBuilder.Query;
            }

        }
    }


    internal static class AuthenticationPropertiesExtensions
    {
        public static void SetIdentityProviderName(this AuthenticationProperties properties, string name) => properties.Items["IdentityProviderName"] = name;
        public static string GetIdentityProviderName(this AuthenticationProperties properties) => properties.Items["IdentityProviderName"];

        public static void SetAuthenticationRequest(this AuthenticationProperties properties, AuthnRequestType request) =>
            properties.Items["AuthenticationRequest"] = SamlHelper.SerializeMessage(request);
        public static AuthnRequestType GetAuthenticationRequest(this AuthenticationProperties properties) =>
            SamlHelper.DeserializeMessage<AuthnRequestType>(properties.Items["AuthenticationRequest"]);

        public static void SetLogoutRequest(this AuthenticationProperties properties, LogoutRequestType request) =>
            properties.Items["LogoutRequest"] = SamlHelper.SerializeMessage(request);
        public static LogoutRequestType GetLogoutRequest(this AuthenticationProperties properties) =>
            SamlHelper.DeserializeMessage<LogoutRequestType>(properties.Items["LogoutRequest"]);

        public static void SetSubjectNameId(this AuthenticationProperties properties, string subjectNameId) => properties.Items["subjectNameId"] = subjectNameId;
        public static string GetSubjectNameId(this AuthenticationProperties properties) => properties.Items["subjectNameId"];

        public static void SetSessionIndex(this AuthenticationProperties properties, string sessionIndex) => properties.Items["SessionIndex"] = sessionIndex;
        public static string GetSessionIndex(this AuthenticationProperties properties) => properties.Items["SessionIndex"];

        public static void SetCorrelationProperty(this AuthenticationProperties properties, string correlationProperty) => properties.Items[".xsrf"] = correlationProperty;
        public static string GetCorrelationProperty(this AuthenticationProperties properties) => properties.Items[".xsrf"];

        public static void Save(this AuthenticationProperties properties, HttpResponse response, ISecureDataFormat<AuthenticationProperties> encryptor)
        {
            response.Cookies.Append("SPID-Properties", encryptor.Protect(properties));
        }

        public static void Load(this AuthenticationProperties properties, HttpRequest request, ISecureDataFormat<AuthenticationProperties> encryptor)
        {
            AuthenticationProperties cookieProperties = encryptor.Unprotect(request.Cookies["SPID-Properties"]);
            properties.AllowRefresh = cookieProperties.AllowRefresh;
            properties.ExpiresUtc = cookieProperties.ExpiresUtc;
            properties.IsPersistent = cookieProperties.IsPersistent;
            properties.IssuedUtc = cookieProperties.IssuedUtc;
            foreach (var item in cookieProperties.Items)
            {
                properties.Items.Add(item);
            }
            foreach (var item in cookieProperties.Parameters)
            {
                properties.Parameters.Add(item);
            }
            properties.RedirectUri = cookieProperties.RedirectUri;
        }
    }
}
