using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SceneBuilder.Editor.Licensing;

// Pins the licensing wire DTOs and the ILicenseTransport seam: request builders emit
// exactly the fields their action requires, ParseResponse round-trips every response
// variant the backend sends, and a mock transport's Completed/Unreachable outcomes
// stay distinguishable end to end.
public class LicenseTransportTests
{
    [TearDown]
    public void TearDown()
    {
        LicenseEndpoint.ResetToDefault();
    }

    private sealed class MockTransport : ILicenseTransport
    {
        private readonly LicenseHttpResponse _response;

        public MockTransport(LicenseHttpResponse response)
        {
            _response = response;
        }

        public Task<LicenseHttpResponse> SendAsync(string requestJson, CancellationToken ct = default)
        {
            return Task.FromResult(_response);
        }
    }

    [Test]
    public void ActivateRequest_ContainsActionAndAllFourFields()
    {
        string json = LicenseWire.ActivateRequest("KEY-1", "raw-machine-id", "My Mac", "macOS");

        StringAssert.Contains("\"action\":\"activate\"", json);
        StringAssert.Contains("\"licenseKey\":\"KEY-1\"", json);
        StringAssert.Contains("\"machineId\":\"raw-machine-id\"", json);
        StringAssert.Contains("\"label\":\"My Mac\"", json);
        StringAssert.Contains("\"os\":\"macOS\"", json);
    }

    [Test]
    public void TrialRequest_OmitsLicenseKey()
    {
        string json = LicenseWire.TrialRequest("raw-machine-id", "My Mac", "macOS");

        StringAssert.Contains("\"action\":\"trial\"", json);
        StringAssert.Contains("\"machineId\":\"raw-machine-id\"", json);
        StringAssert.DoesNotContain("licenseKey", json,
            "A trial request must never carry a licenseKey field on the wire.");
    }

    [Test]
    public void SeatsRequest_ContainsActionAndLicenseKeyOnly()
    {
        string json = LicenseWire.SeatsRequest("KEY-1");

        StringAssert.Contains("\"action\":\"seats\"", json);
        StringAssert.Contains("\"licenseKey\":\"KEY-1\"", json);
    }

    [Test]
    public void ReleaseRequest_ContainsActionLicenseKeyAndHash()
    {
        string json = LicenseWire.ReleaseRequest("KEY-1", "machine-hash-abc");

        StringAssert.Contains("\"action\":\"release\"", json);
        StringAssert.Contains("\"licenseKey\":\"KEY-1\"", json);
        StringAssert.Contains("\"hash\":\"machine-hash-abc\"", json);
    }

    [Test]
    public void ParseResponse_ActivateSuccess_PopulatesTokenAndSeatCounts()
    {
        var result = LicenseWire.ParseResponse("{\"ok\":true,\"token\":\"T\",\"seatsUsed\":2,\"seatsTotal\":3}");

        Assert.IsTrue(result.ok);
        Assert.AreEqual("T", result.token);
        Assert.AreEqual(2, result.seatsUsed);
        Assert.AreEqual(3, result.seatsTotal);
    }

    [Test]
    public void ParseResponse_SeatLimitReject_PopulatesReasonAndSeatsWithLabelAndOs()
    {
        var result = LicenseWire.ParseResponse(
            "{\"ok\":false,\"reason\":\"seat_limit\",\"seats\":["
            + "{\"hash\":\"h1\",\"label\":\"Mac 1\",\"os\":\"macOS\",\"activatedAt\":1000,\"lastSeenAt\":2000},"
            + "{\"hash\":\"h2\",\"label\":\"PC\",\"os\":\"Windows\",\"activatedAt\":1000,\"lastSeenAt\":2000},"
            + "{\"hash\":\"h3\",\"label\":\"Linux Box\",\"os\":\"Linux\",\"activatedAt\":1000,\"lastSeenAt\":2000}"
            + "]}");

        Assert.IsFalse(result.ok);
        Assert.AreEqual(LicenseReason.SeatLimit, result.reason);
        Assert.AreEqual(3, result.seats.Length);
        Assert.AreEqual("Mac 1", result.seats[0].label);
        Assert.AreEqual("macOS", result.seats[0].os);
    }

    [Test]
    public void ParseResponse_SeatsSuccess_PopulatesMachinesWithLabelAndOs()
    {
        var result = LicenseWire.ParseResponse(
            "{\"ok\":true,\"machines\":["
            + "{\"hash\":\"h1\",\"label\":\"Mac 1\",\"os\":\"macOS\",\"activatedAt\":1000,\"lastSeenAt\":2000}"
            + "]}");

        Assert.IsTrue(result.ok);
        Assert.AreEqual(1, result.machines.Length);
        Assert.AreEqual("Mac 1", result.machines[0].label);
        Assert.AreEqual("macOS", result.machines[0].os);
    }

    [Test]
    public void ParseResponse_TrialSuccess_PopulatesTokenAndLeavesSeatListsNull()
    {
        var result = LicenseWire.ParseResponse("{\"ok\":true,\"token\":\"T\"}");

        Assert.IsTrue(result.ok);
        Assert.AreEqual("T", result.token);
        Assert.IsNull(result.seats);
        Assert.IsNull(result.machines);
    }

    [Test]
    public void ParseResponse_MalformedBody_ReturnsNotOkWithoutThrowing()
    {
        LicenseResponse empty = null;
        LicenseResponse notJson = null;

        Assert.DoesNotThrow(() => empty = LicenseWire.ParseResponse(""));
        Assert.DoesNotThrow(() => notJson = LicenseWire.ParseResponse("not json"));

        Assert.IsFalse(empty.ok, "An empty response body must parse to a non-ok response, never throw.");
        Assert.IsFalse(notJson.ok, "A malformed response body must parse to a non-ok response, never throw.");
    }

    [Test]
    public async Task MockTransport_ActivateRoundTrip_ProducesTokenAndSeatCounts()
    {
        const string canned = "{\"ok\":true,\"token\":\"T\",\"seatsUsed\":2,\"seatsTotal\":3}";
        ILicenseTransport transport = new MockTransport(LicenseHttpResponse.Completed(200, canned));

        string requestJson = LicenseWire.ActivateRequest("KEY-1", "raw-machine-id", "My Mac", "macOS");
        LicenseHttpResponse httpResponse = await transport.SendAsync(requestJson);
        LicenseResponse parsed = LicenseWire.ParseResponse(httpResponse.Body);

        Assert.AreEqual(TransportOutcome.Completed, httpResponse.Outcome);
        Assert.IsTrue(parsed.ok);
        Assert.AreEqual("T", parsed.token);
        Assert.AreEqual(2, parsed.seatsUsed);
        Assert.AreEqual(3, parsed.seatsTotal);
    }

    [Test]
    public void LicenseHttpResponse_CompletedAndUnreachable_AreDistinguishableByOutcome()
    {
        LicenseHttpResponse completed = LicenseHttpResponse.Completed(429, "{\"ok\":false,\"reason\":\"rate_limited\"}");
        LicenseHttpResponse unreachable = LicenseHttpResponse.Unreachable();

        Assert.AreEqual(TransportOutcome.Completed, completed.Outcome,
            "A 4xx/5xx response with a body is a reachable server that answered, not Unreachable.");
        Assert.AreEqual(TransportOutcome.Unreachable, unreachable.Outcome);
        Assert.AreNotEqual(completed.Outcome, unreachable.Outcome);
    }

    [Test]
    public void LicenseEndpoint_UrlIsOverridableAndResettable()
    {
        Assert.AreEqual(LicenseEndpoint.Default, LicenseEndpoint.Url);

        LicenseEndpoint.Url = "http://localhost:5001/license";
        Assert.AreEqual("http://localhost:5001/license", LicenseEndpoint.Url);

        LicenseEndpoint.ResetToDefault();
        Assert.AreEqual(LicenseEndpoint.Default, LicenseEndpoint.Url);
    }

    [Test]
    public void LicenseReason_ExposesAllSixWireStringsVerbatim()
    {
        Assert.AreEqual("invalid_key", LicenseReason.InvalidKey);
        Assert.AreEqual("refunded", LicenseReason.Refunded);
        Assert.AreEqual("seat_limit", LicenseReason.SeatLimit);
        Assert.AreEqual("rate_limited", LicenseReason.RateLimited);
        Assert.AreEqual("trial_expired", LicenseReason.TrialExpired);
        Assert.AreEqual("invalid_request", LicenseReason.InvalidRequest);
    }
}
