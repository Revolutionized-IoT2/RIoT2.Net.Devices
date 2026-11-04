using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RIoT2.Net.Devices.Services.Eufy.Cloud;

namespace RIoT2.Net.Devices.Tests.Eufy.Cloud;

/// <summary>A request the fake gateway received; <see cref="Json"/> is the decrypted (or plain) JSON body.</summary>
internal sealed record FakeRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string RawBody, JsonElement? Json)
{
    public string Host => Uri.Host;
    public string Path => Uri.AbsolutePath;
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string? BodyString(string name) =>
        Json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What a route answers: an envelope <c>{code, msg, data}</c>, with <c>data</c> encrypted under the session key.</summary>
internal sealed record FakeReply(int Status, int Code, string? Msg = null, object? Data = null, bool Encrypt = true)
{
    public static FakeReply Ok(object? data = null) => new(200, 0, "ok", data ?? new { });
    public static FakeReply Error(int status, int code, string msg, object? data = null) => new(status, code, msg, data);
}

/// <summary>
/// An in-memory eufy gateway performing the real server side of algo_ecdh: it answers key exchanges with a fresh
/// P-256 key, checks the x-signature, decrypts bodies and encrypts replies. Routes are scripted per test.
/// </summary>
internal sealed class FakeEufyCloud : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string> _keys = new();
    private readonly List<FakeRequest> _requests = [];

    public string EstimatedDomain { get; set; } = "mega-eu-pr.eufy.com";
    /// <summary>Answers a signed request; null falls back to the default routes (login ok, empty data).</summary>
    public Func<FakeRequest, FakeReply?>? Route { get; set; }
    /// <summary>Answers any request before the gateway logic (media downloads, GETs).</summary>
    public Func<FakeRequest, HttpResponseMessage?>? Raw { get; set; }
    /// <summary>Held key exchanges wait for this before answering.</summary>
    public TaskCompletionSource? ExchangeGate { get; set; }
    public int KeyExchanges;
    public int Logins;
    public string LoginToken { get; set; } = "token-1";

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    public IEnumerable<FakeRequest> To(string pathSuffix) => Requests.Where(r => r.Path.EndsWith(pathSuffix, StringComparison.Ordinal));

    /// <summary>The gateway forgets every key-ident (as after a server-side rotation).</summary>
    public void ForgetKeys() => _keys.Clear();

    /// <summary>Registers a key-ident the gateway knows, as for a restored session.</summary>
    public void KnowKey(string keyIdent, string shareKey) => _keys[keyIdent] = shareKey;

    public static HttpResponseMessage JsonResponse(int status, object envelope) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers.NonValidated)
            headers[h.Key] = h.Value.ToString();
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers.NonValidated)
                headers[h.Key] = h.Value.ToString();
        }
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        string? shareKey = null;
        JsonElement? json = null;
        var isExchange = path.EndsWith("/oauth/key/exchange", StringComparison.Ordinal);
        var plain = isExchange || path.EndsWith("/passport/estimate_domain", StringComparison.Ordinal) || request.Method == HttpMethod.Get;
        if (plain)
        {
            json = TryParse(raw);
        }
        else if (headers.TryGetValue("x-key-ident", out var ident) && _keys.TryGetValue(ident, out var key))
        {
            shareKey = key;
            json = TryParse(Encoding.UTF8.GetString(EufyCrypto.DecryptBody(raw, key)));
        }

        var fake = new FakeRequest(request.Method.Method, uri, headers, raw, json);
        lock (_requests)
            _requests.Add(fake);

        if (Raw?.Invoke(fake) is { } rawResponse)
            return rawResponse;

        if (path.EndsWith("/passport/estimate_domain", StringComparison.Ordinal))
            return JsonResponse(200, new { code = 0, msg = "ok", data = new { domain = EstimatedDomain } });

        if (isExchange)
        {
            Interlocked.Increment(ref KeyExchanges);
            if (ExchangeGate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            return KeyExchange(uri.Host, fake);
        }

        if (shareKey is null)
            return JsonResponse(463, new { code = 4404, msg = "get identity error" });

        var expected = EufyCrypto.SignRequest(shareKey, headers["x-request-ts"], headers["x-request-once"], raw);
        if (!string.Equals(expected, headers.GetValueOrDefault("x-signature"), StringComparison.Ordinal))
            return JsonResponse(403, new { code = 4416, msg = "signature invalid" });

        var reply = Route?.Invoke(fake) ?? DefaultRoute(fake);
        object? data = reply.Data;
        if (data is not null && reply.Encrypt)
            data = EufyCrypto.EncryptBody(JsonSerializer.Serialize(data), shareKey);
        return JsonResponse(reply.Status, new { code = reply.Code, msg = reply.Msg, data });
    }

    /// <summary>The default answers: a login with <see cref="LoginToken"/>, anything else an empty success.</summary>
    public FakeReply DefaultRoute(FakeRequest request)
    {
        if (request.Path.EndsWith("/passport/login", StringComparison.Ordinal))
            return LoginOk();
        return FakeReply.Ok(new { });
    }

    public FakeReply LoginOk(string userId = "user-1", string? apCloudUserId = null, string? token = null, object? faInfo = null)
    {
        Interlocked.Increment(ref Logins);
        return FakeReply.Ok(new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["ap_cloud_user_id"] = apCloudUserId ?? userId,
            ["auth_token"] = token ?? LoginToken,
            ["token_expires_at"] = 0,
            ["fa_info"] = faInfo ?? new { info = "" },
        });
    }

    private HttpResponseMessage KeyExchange(string host, FakeRequest request)
    {
        var localKey = Convert.FromHexString(host.Contains(".eufylife.com", StringComparison.Ordinal) ? EufyCrypto.EufylifeLocalKeyHex : EufyCrypto.EufyMegaLocalKeyHex);
        var wrapped = request.Json?.GetProperty("client_public_key").GetString() ?? "";
        string clientPublicHex;
        try
        {
            clientPublicHex = Encoding.UTF8.GetString(EufyCrypto.UnwrapCbc(localKey, Convert.FromBase64String(wrapped)));
        }
        catch (CryptographicException)
        {
            return JsonResponse(403, new { code = 4416, msg = "bad local key" });
        }
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var shared = EufyCrypto.DeriveSecret(server, clientPublicHex);
        _keys[request.Header("x-key-ident")!] = Convert.ToHexStringLower(shared)[..32];
        var serverKey = Convert.ToBase64String(EufyCrypto.WrapCbc(localKey, Encoding.UTF8.GetBytes(EufyCrypto.PublicKeyHex(server))));
        return JsonResponse(200, new { code = 0, msg = "ok", data = new { server_public_key = serverKey } });
    }

    private static JsonElement? TryParse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
