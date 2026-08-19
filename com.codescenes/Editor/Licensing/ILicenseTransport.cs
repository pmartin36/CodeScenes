using System.Threading;
using System.Threading.Tasks;

namespace SceneBuilder.Editor.Licensing
{
    // Distinguishes a server that answered (any HTTP status, including a 4xx/5xx
    // reject with a body) from one that could not be reached at all. Callers branch
    // on this: an unreachable server stays silent, a reachable server that rejects
    // acts immediately.
    public enum TransportOutcome
    {
        Completed,
        Unreachable
    }

    public readonly struct LicenseHttpResponse
    {
        public readonly TransportOutcome Outcome;
        public readonly long StatusCode;
        public readonly string Body;

        public LicenseHttpResponse(TransportOutcome outcome, long statusCode, string body)
        {
            Outcome = outcome;
            StatusCode = statusCode;
            Body = body;
        }

        public static LicenseHttpResponse Completed(long statusCode, string body) =>
            new LicenseHttpResponse(TransportOutcome.Completed, statusCode, body);

        public static LicenseHttpResponse Unreachable() =>
            new LicenseHttpResponse(TransportOutcome.Unreachable, 0, null);
    }

    // The single injectable network seam every licensing flow calls and every
    // EditMode test mocks. requestJson already carries {action,...}; the transport
    // is action-agnostic since there is one endpoint.
    public interface ILicenseTransport
    {
        Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default);
    }
}
