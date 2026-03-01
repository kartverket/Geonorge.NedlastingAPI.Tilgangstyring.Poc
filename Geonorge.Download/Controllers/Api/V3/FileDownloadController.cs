using Asp.Versioning;
using Geonorge.Download.Models;
using Geonorge.Download.Services;
using Geonorge.Download.Services.Auth;
using Geonorge.Download.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;

namespace Geonorge.Download.Controllers.Api.V3
{
    [ApiController]
    [ApiVersion("3.0")]
    [Tags("Download")] // Groups together with DownloadControllers endpoints
    [ApiExplorerSettings(GroupName = "latest")]
    [Route("api")]
    [Route("api/v{version:apiVersion}")]
    [AllowAnonymous]
    //[Authorize(AuthenticationSchemes = $"{BasicMachineAuthHandler.SchemeName},{JwtBearerDefaults.AuthenticationScheme}")]
    public class FileDownloadController(ILogger<FileDownloadController> logger, IConfiguration config, IFileService fileService, IDownloadService downloadService) : ControllerBase
    {
        private static readonly string[] AuthSchemes =
        {
            CookieAuthenticationDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme,
            BasicMachineAuthHandler.SchemeName
        };

        /// <summary>
        /// Download a file directly based on dataset uuid and file uuid. This method is used by the atom feed and desktop download client.
        /// Restricted files are secured with BAAT-authentication (SAML) and local machine accounts (Basic authentication)
        /// </summary>
        /// <param name="datasetUuid">metadata uuid of the dataset</param>
        /// <param name="fileUuid">the file uuid</param>
        /// <returns></returns>
        [HttpGet("download/file/{datasetUuid}/{fileUuid}")]
        [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "application/octet-stream")]
        [ProducesResponseType(StatusCodes.Status302Found)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/json", "application/xml")]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/json", "application/xml")]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/json", "application/xml")]
        public async Task<IActionResult> GetFile(string datasetUuid, string fileUuid)
        {
            if (!DownloadController.IsValidUuid(datasetUuid))
                return BadRequest("datasetUuid is not a valid uuid");

            if (!DownloadController.IsValidUuid(fileUuid))
                return BadRequest("fileUuid is not a valid uuid");

            var dataset = await fileService.GetDatasetAsync(datasetUuid);
            if (dataset is null) return NotFound();

            var file = await fileService.GetFileAsync(fileUuid, datasetUuid);
            if (file is null) return NotFound();

            var isRestricted = dataset.IsRestricted();

            // Public dataset: allow anonymous, no auth required
            if (!isRestricted)
            {
                logger.LogInformation("Serving PUBLIC [file={File}] [dataset={Dataset}]",
                    file.Filename, dataset.Title);

                await downloadService.StreamRemoteFileToResponseAsync(HttpContext, file.Url);
                return new EmptyResult();
            }

            // Restricted: try authenticate via cookie OR bearer OR machine basic
            var isAuthenticated = await TryAuthenticateAnyAsync(HttpContext);

            if (!isAuthenticated)
            {
                logger.LogInformation(
                    "Access denied to [file={File}]. [dataset={Dataset}] is restricted and caller is anonymous.",
                    file.Filename, dataset.Title);

                if (IsBrowserNavigation(Request))
                {
                    // Local returnUrl only (path + query), avoid absolute host/scheme and avoid //
                    var returnUrl = $"{Request.PathBase}{Request.Path}{Request.QueryString}";
                    return Redirect($"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
                }

                return Unauthorized(); // API client gets 401
            }

            // Authenticated but still needs access/role
            var userHasAccess = fileService.HasAccess(file, User);
            if (!userHasAccess)
            {
                logger.LogInformation(
                    "Access denied to [file={File}]. [dataset={Dataset}] is restricted and user lacks access/role.",
                    file.Filename, dataset.Title);

                return Forbid(); // 403, no redirect loop
            }

            logger.LogInformation("Serving RESTRICTED [file={File}] [dataset={Dataset}]",
                file.Filename, dataset.Title);

            await downloadService.StreamRemoteFileToResponseAsync(HttpContext, file.Url);
            return new EmptyResult();
        }

        private async Task<bool> TryAuthenticateAnyAsync(HttpContext http)
        {
            if (http.User?.Identity?.IsAuthenticated == true)
                return true;

            foreach (var scheme in AuthSchemes)
            {
                var result = await http.AuthenticateAsync(scheme);
                if (result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true)
                {
                    http.User = result.Principal; // ensure HasAccess sees correct principal
                    return true;
                }
            }

            return false;
        }

        private static bool IsBrowserNavigation(HttpRequest req)
        {
            var fetchMode = req.Headers["Sec-Fetch-Mode"].ToString();
            if (string.Equals(fetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
                return true;

            var accept = req.Headers.Accept.ToString();
            return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        }
    }
}