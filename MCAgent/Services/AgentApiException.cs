using System.Net;

namespace MCAgent.Services;

public sealed class AgentApiException : Exception
{
    public AgentApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
