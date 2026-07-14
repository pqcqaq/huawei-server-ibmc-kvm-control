using System.Net;
using System.Net.Security;

namespace IbmcKvm.Protocol.Login;

public enum ServerCertificatePolicy
{
    Strict,
    AllowUntrustedForSession,
}

public sealed class IbmcLoginClient(HttpClient httpClient, TimeSpan requestTimeout)
{
    private const int MaximumResponseLength = 4096;

    public async Task<IbmcLoginResponse> LoginAsync(
        IbmcEndpoint endpoint,
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            throw new ArgumentException("The user name is required.", nameof(request));
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            throw new ArgumentException("The password is required.", nameof(request));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.LoginUri)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("check_pwd", request.Password),
                new KeyValuePair<string, string>("logtype", "1"),
                new KeyValuePair<string, string>("user_name", request.UserName),
                new KeyValuePair<string, string>("func", "DirectKVM"),
                new KeyValuePair<string, string>("IsKvmApp", "1"),
                new KeyValuePair<string, string>("KvmMode", ((int)request.Mode).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
        };

        using var response = await httpClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await ReadLimitedBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
        return LegacyLoginResponseParser.Parse(body);
    }

    public static HttpClient CreateHttpClient(ServerCertificatePolicy certificatePolicy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        if (certificatePolicy == ServerCertificatePolicy.AllowUntrustedForSession)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, errors) =>
                errors == SslPolicyErrors.None || errors != SslPolicyErrors.None;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static async Task<string> ReadLimitedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var buffer = new char[MaximumResponseLength + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaximumResponseLength || await HasMoreContentAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The iBMC login response exceeded the maximum size.");
        }

        return new string(buffer, 0, total).TrimEnd('\r', '\n');
    }

    private static async Task<bool> HasMoreContentAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var trailing = new char[1];
        return await reader.ReadAsync(trailing.AsMemory(), cancellationToken).ConfigureAwait(false) != 0;
    }
}

