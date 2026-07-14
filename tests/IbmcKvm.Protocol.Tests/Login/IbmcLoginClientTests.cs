using System.Net;
using System.Text;
using IbmcKvm.Protocol.Login;

namespace IbmcKvm.Protocol.Tests.Login;

public sealed class IbmcLoginClientTests
{
    [Fact]
    public async Task LoginAsyncPostsObservedFormFields()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "[0][123][\"0011\"][4][1][0][8208][8209]<SN>");
        using var httpClient = new HttpClient(handler);
        var client = new IbmcLoginClient(httpClient, TimeSpan.FromSeconds(2));
        var endpoint = IbmcEndpoint.Parse("ibmc.example.test:8443");

        var response = await client.LoginAsync(
            endpoint,
            new LoginRequest("admin user", "p@ss&word", ConnectionMode.Exclusive),
            CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.NotNull(handler.Request);
        Assert.Equal(endpoint.LoginUri, handler.Request.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal(
            "check_pwd=p%40ss%26word&logtype=1&user_name=admin+user&func=DirectKVM&IsKvmApp=1&KvmMode=1",
            handler.Body);
    }

    [Fact]
    public async Task LoginAsyncRejectsOversizedResponses()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, new string('x', 4097));
        using var httpClient = new HttpClient(handler);
        var client = new IbmcLoginClient(httpClient, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.LoginAsync(
            IbmcEndpoint.Parse("ibmc.example.test"),
            new LoginRequest("admin", "secret", ConnectionMode.Shared),
            CancellationToken.None));
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/plain"),
                RequestMessage = request,
            };
        }
    }
}

