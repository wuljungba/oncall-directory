using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OnCallApi.Configuration;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// What the delta read actually sends to Graph, and what it does with the answers.
///
/// This sits below <see cref="IGraphApiService"/> on purpose. The bug being fixed was invisible
/// from above: the service accepted a delta token, never put it on the request, and read only
/// the first page — so a faked interface would have reported perfect health while the real one
/// re-enumerated page one forever and the caller deactivated everybody it never saw.
///
/// The stub transport records every URL requested, which is the only way to prove otherwise.
/// </summary>
public class GraphDeltaPagingTests
{
    private const string BaseUrl = "https://graph.test/v1.0";
    private const string Page2Url = BaseUrl + "/users/delta?$skiptoken=page-two";
    private const string FinalDeltaLink = BaseUrl + "/users/delta?$deltatoken=final";

    /// <summary>Answers a scripted sequence and remembers what it was asked for.</summary>
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<string> RequestedUrls { get; } = [];

        public ScriptedTransport(params HttpResponseMessage[] responses) =>
            _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"Unscripted request to {request.RequestUri}");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StubClientFactory(GraphServiceClient client) : IGraphClientFactory
    {
        public GraphServiceClient For(string? entraTenantId) => client;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string UserJson(string id, string name) =>
        $$"""{"id":"{{id}}","givenName":"{{name}}","surname":"Example","mail":"{{id}}@example.test"}""";

    // Concatenated rather than an interpolated raw string: this JSON ends in two closing braces,
    // which a $$"""…""" literal would swallow as an escape.
    private static string RemovedJson(string id) =>
        "{\"id\":\"" + id + "\",\"@removed\":{\"reason\":\"deleted\"}}";

    private static (GraphApiService Service, ScriptedTransport Transport) Create(params HttpResponseMessage[] responses)
    {
        var transport = new ScriptedTransport(responses);
        var client = new GraphServiceClient(
            new HttpClient(transport), new AnonymousAuthenticationProvider(), BaseUrl);

        var service = new GraphApiService(
            Options.Create(new GraphApiOptions { TenantId = "home-directory" }),
            new StubClientFactory(client),
            NullLogger<GraphApiService>.Instance);

        return (service, transport);
    }

    [Fact]
    public async Task EveryPageIsFollowedToTheEnd()
    {
        var (service, transport) = Create(
            Json($$"""{"value":[{{UserJson("alice", "Alice")}}],"@odata.nextLink":"{{Page2Url}}"}"""),
            Json($$"""{"value":[{{UserJson("bob", "Bob")}}],"@odata.deltaLink":"{{FinalDeltaLink}}"}"""));

        var result = await service.SyncUsersDeltaAsync(null, null);

        result.Users.Should().HaveCount(2, "the directory spans two pages and both were read");
        result.PagesRead.Should().Be(2);
        result.Completed.Should().BeTrue();
        result.WasFullEnumeration.Should().BeTrue();
        result.DeltaLink.Should().Be(FinalDeltaLink);
        transport.RequestedUrls.Should().HaveCount(2);
        transport.RequestedUrls[1].Should().Contain("$skiptoken=page-two", "page two is fetched from the nextLink");
    }

    /// <summary>The regression that started all of this: the cursor was stored and never sent.</summary>
    [Fact]
    public async Task AStoredDeltaLinkIsTheUrlActuallyRequested()
    {
        const string stored = BaseUrl + "/users/delta?$deltatoken=stored-cursor";
        var (service, transport) = Create(
            Json($$"""{"value":[],"@odata.deltaLink":"{{FinalDeltaLink}}"}"""));

        var result = await service.SyncUsersDeltaAsync(null, stored);

        transport.RequestedUrls.Should().ContainSingle();
        transport.RequestedUrls[0].Should().Contain("$deltatoken=stored-cursor");
        result.WasFullEnumeration.Should().BeFalse("resuming from a cursor is not a full enumeration");
    }

    [Fact]
    public async Task AFreshEnumerationAsksOnlyForTheFieldsItMaps()
    {
        var (service, transport) = Create(
            Json($$"""{"value":[],"@odata.deltaLink":"{{FinalDeltaLink}}"}"""));

        await service.SyncUsersDeltaAsync(null, null);

        var url = Uri.UnescapeDataString(transport.RequestedUrls[0]);
        url.Should().Contain("$select=");
        url.Should().Contain("mobilePhone").And.Contain("accountEnabled");
        // All three are load-bearing for ResolveEmail; dropping one silently skips every user.
        url.Should().Contain("mail").And.Contain("otherMails").And.Contain("userPrincipalName");
    }

    [Fact]
    public async Task AResumedRequestReplaysTheStoredUrlWithoutReapplyingQueryParameters()
    {
        const string stored = BaseUrl + "/users/delta?$deltatoken=stored-cursor";
        var (service, transport) = Create(
            Json($$"""{"value":[],"@odata.deltaLink":"{{FinalDeltaLink}}"}"""));

        await service.SyncUsersDeltaAsync(null, stored);

        // The stored URL already encodes its query. Re-applying $select corrupts a resumption.
        Uri.UnescapeDataString(transport.RequestedUrls[0]).Should().NotContain("$select=");
    }

    /// <summary>
    /// A stored value carrying a skiptoken is a mid-enumeration cursor from the old code. Replaying
    /// one returns the tail of a stale page set and presents it as the whole directory.
    /// </summary>
    [Fact]
    public async Task AStoredSkiptokenIsDiscardedInFavourOfAFullEnumeration()
    {
        const string poisoned = BaseUrl + "/users/delta?$skiptoken=half-way";
        var (service, transport) = Create(
            Json($$"""{"value":[],"@odata.deltaLink":"{{FinalDeltaLink}}"}"""));

        var result = await service.SyncUsersDeltaAsync(null, poisoned);

        result.WasFullEnumeration.Should().BeTrue();
        transport.RequestedUrls[0].Should().NotContain("half-way");
    }

    [Fact]
    public async Task ReportedRemovalsAreCollectedRatherThanDiscarded()
    {
        var (service, _) = Create(
            Json($$"""
                {"value":[{{UserJson("alice", "Alice")}},{{RemovedJson("bob")}}],
                 "@odata.deltaLink":"{{FinalDeltaLink}}"}
                """));

        var result = await service.SyncUsersDeltaAsync(null, null);

        result.Users.Should().ContainSingle().Which.AzureAdObjectId.Should().Be("alice");
        result.RemovedObjectIds.Should().ContainSingle().Which.Should().Be("bob");
    }

    [Fact]
    public async Task AFailureHalfWayThroughKeepsWhatWasReadAndStoresNoCursor()
    {
        var (service, _) = Create(
            Json($$"""{"value":[{{UserJson("alice", "Alice")}}],"@odata.nextLink":"{{Page2Url}}"}"""),
            Json("""{"error":{"code":"serviceNotAvailable","message":"try later"}}""", HttpStatusCode.ServiceUnavailable));

        var result = await service.SyncUsersDeltaAsync(null, null);

        result.Completed.Should().BeFalse();
        result.DeltaLink.Should().BeNull("a cursor here would skip everyone on the pages never read");
        result.Users.Should().ContainSingle("page one was read and is still worth upserting");
        result.FailureDetail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnExpiredCursorCausesExactlyOneFullReEnumeration()
    {
        const string stored = BaseUrl + "/users/delta?$deltatoken=too-old";
        var (service, transport) = Create(
            Json("""{"error":{"code":"syncStateNotFound","message":"resync required"}}""", HttpStatusCode.Gone),
            Json($$"""{"value":[{{UserJson("alice", "Alice")}}],"@odata.deltaLink":"{{FinalDeltaLink}}"}"""));

        var result = await service.SyncUsersDeltaAsync(null, stored);

        result.TokenWasRejected.Should().BeTrue();
        result.WasFullEnumeration.Should().BeTrue();
        result.Completed.Should().BeTrue();
        result.Users.Should().ContainSingle();
        transport.RequestedUrls.Should().HaveCount(2, "one rejected attempt and one full re-enumeration, no loop");
        transport.RequestedUrls[1].Should().NotContain("too-old");
    }

    [Fact]
    public async Task ARefusalIsNotMistakenForAnExpiredCursor()
    {
        const string stored = BaseUrl + "/users/delta?$deltatoken=still-good";
        var (service, transport) = Create(
            Json("""{"error":{"code":"Authorization_RequestDenied","message":"insufficient privileges"}}""",
                HttpStatusCode.Forbidden));

        var result = await service.SyncUsersDeltaAsync(null, stored);

        result.Completed.Should().BeFalse();
        result.TokenWasRejected.Should().BeFalse("a permissions problem is not a stale cursor");
        transport.RequestedUrls.Should().ContainSingle("re-enumerating would not have helped and costs a full read");
    }

    [Fact]
    public async Task APageWithNeitherLinkIsReportedIncomplete()
    {
        var (service, _) = Create(Json($$"""{"value":[{{UserJson("alice", "Alice")}}]}"""));

        var result = await service.SyncUsersDeltaAsync(null, null);

        result.Completed.Should().BeFalse();
        result.DeltaLink.Should().BeNull();
        result.FailureDetail.Should().Contain("neither a nextLink nor a deltaLink");
    }
}
