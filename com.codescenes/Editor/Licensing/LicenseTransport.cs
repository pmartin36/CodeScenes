using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace SceneBuilder.Editor.Licensing
{
    // Default UnityWebRequest-backed ILicenseTransport. Its live behavior against a
    // reachable server is validated manually against the deployed/emulated backend;
    // the gate suite drives every flow through a mock ILicenseTransport instead.
    public sealed class LicenseTransport : ILicenseTransport
    {
        public Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<LicenseHttpResponse>();

            var request = new UnityWebRequest(LicenseEndpoint.Url, UnityWebRequest.kHttpVerbPOST);
            byte[] body = Encoding.UTF8.GetBytes(requestJson ?? string.Empty);
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            UnityWebRequestAsyncOperation op = request.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    LicenseHttpResponse response;
                    if (request.result == UnityWebRequest.Result.ConnectionError
                        || request.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        response = LicenseHttpResponse.Unreachable();
                    }
                    else
                    {
                        response = LicenseHttpResponse.Completed(request.responseCode, request.downloadHandler.text);
                    }

                    tcs.TrySetResult(response);
                }
                finally
                {
                    request.Dispose();
                }
            };

            return tcs.Task;
        }
    }
}
