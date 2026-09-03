public sealed class OpaAuthorizationClient(
    HttpClient httpClient,
    IHttpContextAccessor contextAccessor)
{
    public async Task<bool> IsAllowedAsync(
        string action,
        string resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var authorization = contextAccessor.HttpContext?
            .Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorization))
            return false;

        var request = new
        {
            input = new
            {
                headers = new Dictionary<string, string>
                {
                    ["authorization"] = authorization
                },
                action,
                resource = new
                {
                    type = resourceType,
                    id = resourceId
                }
            }
        };

        using var response =
            await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
                httpClient,
                "/v1/data/authorization/allow",
                request,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        var decision = await response.Content
            .ReadFromJsonAsync<OpaDecision>(cancellationToken);

        return decision?.Result ?? false;
    }

    private sealed record OpaDecision(bool Result);
}