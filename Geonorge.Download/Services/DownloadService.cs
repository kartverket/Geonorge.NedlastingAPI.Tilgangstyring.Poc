using Geonorge.Download.Services.Interfaces;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace Geonorge.Download.Services
{
    public class DownloadService(ILogger<DownloadService> logger, IConfiguration config, IHttpClientFactory httpClientFactory) : IDownloadService
    {
        public async Task StreamRemoteFileToResponseAsync(HttpContext httpContext, string url)
        {
            const int bufferSize = 64 * 1024; // 64 KB
            var requestAborted = httpContext.RequestAborted;

            try
            {
                var client = httpClientFactory.CreateClient("FileStreamingClient");

                using var remoteResponse = await client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestAborted);

                if (remoteResponse.Content == null)
                {
                    if (!httpContext.Response.HasStarted)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                        await httpContext.Response.WriteAsync("File not found or inaccessible.", requestAborted);
                    }
                    return;
                }

                if (remoteResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    if (!httpContext.Response.HasStarted)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                        await httpContext.Response.WriteAsync("File not found or inaccessible.", requestAborted);
                    }
                    return;
                }

                if (!remoteResponse.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Remote server returned non-success status code {StatusCode} while streaming file from: {Url}",
                        (int)remoteResponse.StatusCode,
                        url);

                    if (!httpContext.Response.HasStarted)
                    {
                        // Choose either passthrough or map to 502. Passthrough is often more honest.
                        httpContext.Response.StatusCode = (int)remoteResponse.StatusCode;
                        await httpContext.Response.WriteAsync("Unable to fetch remote file.", requestAborted);
                    }
                    return;
                }

                await using var remoteStream = await remoteResponse.Content.ReadAsStreamAsync(requestAborted);

                var response = httpContext.Response;

                response.ContentType =
                    remoteResponse.Content.Headers.ContentType?.ToString()
                    ?? "application/octet-stream";

                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    var contentDisposition = new ContentDispositionHeaderValue("attachment")
                    {
                        FileNameStar = fileName
                    };

                    response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
                }

                if (remoteResponse.Content.Headers.ContentLength.HasValue)
                {
                    response.ContentLength = remoteResponse.Content.Headers.ContentLength.Value;
                }

                // Copy upstream stream directly to downstream response body.
                // No manual per-chunk flush: let ASP.NET Core / Kestrel handle buffering.
                await remoteStream.CopyToAsync(response.Body, bufferSize, requestAborted);
            }
            catch (OperationCanceledException ex) when (requestAborted.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Download request was aborted while streaming remote file from: {Url}",
                    url);
            }
            catch (TaskCanceledException ex) when (!requestAborted.IsCancellationRequested)
            {
                // Usually indicates upstream timeout or some other HttpClient-side cancellation.
                logger.LogError(
                    ex,
                    "Upstream request timed out or was canceled while streaming remote file from: {Url}",
                    url);

                if (!httpContext.Response.HasStarted)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await httpContext.Response.WriteAsync(
                        "Timed out while downloading the remote file.",
                        CancellationToken.None);
                }
            }
            catch (IOException ex) when (requestAborted.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Client disconnected while receiving streamed file from: {Url}",
                    url);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unhandled exception while streaming file from: {Url}",
                    url);

                if (!httpContext.Response.HasStarted)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await httpContext.Response.WriteAsync(
                        "An error occurred while streaming the file.",
                        CancellationToken.None);
                }
            }
        }

        public bool AreaIsWithinDownloadLimits(string coordinates, string coordinateSystem, string metadataUuid)
        {
            var areaCheckerApiUrl = MakeAreaCheckerApiUrl(coordinates, coordinateSystem, metadataUuid);

            var areaCheckerResult = CallAreaChecker(areaCheckerApiUrl);

            var areaIsWithinDownloadLimits = areaCheckerResult.Value<bool>("allowed");

            return areaIsWithinDownloadLimits;
        }

        private string MakeAreaCheckerApiUrl(string coordinates, string coordinateSystem, string metadataUuid)
        {
            var areaCheckerUrl = config["FmeAreaChecker"];
            var areaCheckerToken = config["FmeAreaCheckerToken"];

            var urlBuilder = new StringBuilder(areaCheckerUrl);

            urlBuilder.Append("CLIPPERCOORDS=").Append(coordinates);
            urlBuilder.Append("&CLIPPERCOORDSYS=").Append(coordinateSystem);
            urlBuilder.Append("&UUID=").Append(metadataUuid);
            urlBuilder.Append("&token=").Append(areaCheckerToken);

            return urlBuilder.ToString();
        }

        private JObject CallAreaChecker(string url)
        {
            string jsonResult;

            var request = (HttpWebRequest) WebRequest.Create(url);
            logger.LogInformation("Area checker request: " + url);
            try
            {
                var response = request.GetResponse();
                using (var responseStream = response.GetResponseStream())
                {
                    var reader = new StreamReader(responseStream, Encoding.UTF8);
                    jsonResult = reader.ReadToEnd();
                }
                logger.LogInformation("Area checker response: " + ((HttpWebResponse)response).StatusCode + " Body: "+jsonResult);
            }
            catch (WebException exception)
            {
                var errorResponse = exception.Response;
                
                using (var responseStream = errorResponse.GetResponseStream())
                {
                    var reader = new StreamReader(responseStream, Encoding.GetEncoding("utf-8"));
                    var errorText = reader.ReadToEnd();
                    logger.LogError(errorText, exception);
                }
                throw;
            }

            jsonResult = jsonResult.Trim('[', ']'); // [{"allowed":true}] -> {"allowed":true}

            return JObject.Parse(jsonResult);
        }

        public JObject CallClipperFileChecker(string url)
        {
            string jsonResult;

            var request = (HttpWebRequest)WebRequest.Create(url);
            logger.LogInformation("Clipper file checker request: " + url);
            try
            {
                var response = request.GetResponse();
                using (var responseStream = response.GetResponseStream())
                {
                    var reader = new StreamReader(responseStream, Encoding.UTF8);
                    jsonResult = reader.ReadToEnd();
                }
                logger.LogInformation("Clipper file checker response: " + ((HttpWebResponse)response).StatusCode + " Body: " + jsonResult);
            }
            catch (WebException exception)
            {
                var errorResponse = exception.Response;

                using (var responseStream = errorResponse.GetResponseStream())
                {
                    var reader = new StreamReader(responseStream, Encoding.GetEncoding("utf-8"));
                    var errorText = reader.ReadToEnd();
                    logger.LogError(errorText, exception);
                }
                throw;
            }

            jsonResult = jsonResult.Trim('[', ']'); // [{"allowed":true}] -> {"allowed":true}

            return JObject.Parse(jsonResult);
        }
    }
}
