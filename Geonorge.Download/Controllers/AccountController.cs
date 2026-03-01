using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using System;

namespace Geonorge.Download.Controllers
{
    [AllowAnonymous]
    [Route("account")]
    public class AccountController(ILogger<AccountController> logger, IConfiguration config) : Controller
    {
        // GET /account/login
        [HttpGet("login")]
        public IActionResult Login(string? returnUrl = "/")
        {
            returnUrl = NormalizeReturnUrl(returnUrl);

            // If already signed in locally, don't re-challenge (breaks infinite loop)
            if (User?.Identity?.IsAuthenticated == true)
            {
                logger.LogDebug("Already authenticated, redirecting to {ReturnUrl}", returnUrl);
                return LocalRedirect(returnUrl);
            }

            logger.LogDebug("Not authenticated, challenging OIDC. ReturnUrl={ReturnUrl}", returnUrl);

            return Challenge(new AuthenticationProperties
            {
                RedirectUri = returnUrl
            }, OpenIdConnectDefaults.AuthenticationScheme);
        }

        // GET /account/logout
        [HttpGet("logout")]
        public async Task<IActionResult> SignOut()
        {
            var redirectUri = string.IsNullOrWhiteSpace(config["auth:oidc:PostLogoutRedirectUri"]) ? "/" : config["auth:oidc:PostLogoutRedirectUri"];
            var cookieAuth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!cookieAuth.Succeeded)
                // Already logged out locally
                return Redirect(redirectUri!);

            // Try to get the OIDC authenticate result for tokens (requires SaveTokens = true)
            var oidcAuth = await HttpContext.AuthenticateAsync(OpenIdConnectDefaults.AuthenticationScheme);
            var idToken = oidcAuth.Properties?.GetTokenValue("id_token");

            var props = new AuthenticationProperties { RedirectUri = redirectUri };

            // If id_token exists, store it so the OIDC handler can send id_token_hint.
            if (!string.IsNullOrEmpty(idToken))
                props.StoreTokens([new AuthenticationToken { Name = "id_token", Value = idToken }]);

            return SignOut(props, CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);
        }

        [HttpGet("set-culture")]
        public IActionResult SetCulture(string culture, string returnUrl = "/")
        {
            Response.Cookies.Append(
                "_culture",
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    Path = "/"
                });

            return Redirect(returnUrl);
        }

        private string NormalizeReturnUrl(string? returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl))
                return "/";

            // kill accidental double slashes that can create weirdness
            while (returnUrl.Contains("//"))
                returnUrl = returnUrl.Replace("//", "/");

            // Only allow local redirects (prevents open redirect vulnerabilities)
            if (!Url.IsLocalUrl(returnUrl))
                return "/";

            return returnUrl;
        }
    }
}
