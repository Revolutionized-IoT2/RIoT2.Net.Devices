using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Harvia
{
    /// <summary>The tokens of a Cognito login: the ID token AppSync takes, and the refresh token for the next ones.</summary>
    internal sealed record CognitoTokens(string IdToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    /// <summary>
    /// An AWS Cognito user pool login with Secure Remote Password (USER_SRP_AUTH), the only password login the MyHarvia
    /// app client allows, and the refresh of its tokens. The SRP math is Amazon's (amazon-cognito-identity-js'
    /// AuthenticationHelper): the RFC 5054 3072-bit group, g = 2, k = H(N | g), the key HKDF-SHA256 with the info
    /// "Caldera Derived Key". The password never leaves the node.
    /// </summary>
    internal sealed class CognitoSrp
    {
        // RFC 3526 group 15, the 3072-bit MODP prime: 2^3072 - 2^3008 - 1 + 2^64 * (floor(2^2942 * pi) + 1690314).
        internal const string NHex =
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374" +
            "FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598" +
            "DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39" +
            "E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A855" +
            "21ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D876" +
            "02733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

        internal static readonly BigInteger N = Parse(NHex);
        internal static readonly BigInteger G = 2;
        internal static readonly BigInteger K = Parse(HexHash(PadHex(N) + PadHex(G)));

        private static readonly byte[] _info = Encoding.UTF8.GetBytes("Caldera Derived Key");

        // Cognito's answers that mean the e-mail or password is wrong, or the account can't log in.
        private static readonly HashSet<string> _credentialErrors = new(StringComparer.Ordinal)
        {
            "NotAuthorizedException", "UserNotFoundException", "UserNotConfirmedException", "PasswordResetRequiredException"
        };

        private static readonly HashSet<string> _transientErrors = new(StringComparer.Ordinal)
        {
            "TooManyRequestsException", "InternalErrorException", "LimitExceededException", "ServiceUnavailableException"
        };

        private readonly string _clientId;
        private readonly string _poolName;
        private readonly HarviaSend _send;
        private readonly TimeProvider _time;

        public CognitoSrp(string userPoolId, string clientId, HarviaSend send, TimeProvider time)
        {
            var parts = userPoolId.Split('_', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new HarviaUnavailableException($"MyHarvia's user pool id '{userPoolId}' isn't a Cognito pool id.");
            _clientId = clientId;
            _poolName = parts[1];
            Url = $"https://cognito-idp.{parts[0]}.amazonaws.com/";
            _send = send;
            _time = time;
        }

        /// <summary>The Cognito endpoint of the pool's region.</summary>
        public string Url { get; }

        /// <summary>Logs in with the user name (the e-mail) and password.</summary>
        public async Task<CognitoTokens> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            var a = RandomSmallA();
            var bigA = BigInteger.ModPow(G, a, N);
            var challenge = await CallAsync("InitiateAuth", new JsonObject
            {
                ["AuthFlow"] = "USER_SRP_AUTH",
                ["ClientId"] = _clientId,
                ["AuthParameters"] = new JsonObject { ["USERNAME"] = username, ["SRP_A"] = Hex(bigA) }
            }, "the login", cancellationToken).ConfigureAwait(false);

            var name = HarviaJson.Text(challenge["ChallengeName"]);
            if (name != "PASSWORD_VERIFIER")
                throw new HarviaAuthenticationException($"MyHarvia's login asked for {name ?? "nothing"} instead of the password, which RIoT2 can't answer.");
            var parameters = challenge["ChallengeParameters"] as JsonObject ?? [];
            var userId = HarviaJson.Text(parameters["USER_ID_FOR_SRP"]);
            var salt = HarviaJson.Text(parameters["SALT"]);
            var srpB = HarviaJson.Text(parameters["SRP_B"]);
            var secretBlock = HarviaJson.Text(parameters["SECRET_BLOCK"]);
            if (userId == null || salt == null || srpB == null || secretBlock == null)
                throw new HarviaUnavailableException("MyHarvia's login challenge is incomplete.");

            var timestamp = Timestamp(_time.GetUtcNow());
            var signature = Signature(_poolName, userId, password, a, bigA, salt, srpB, secretBlock, timestamp);
            var answer = await CallAsync("RespondToAuthChallenge", new JsonObject
            {
                ["ChallengeName"] = "PASSWORD_VERIFIER",
                ["ClientId"] = _clientId,
                ["ChallengeResponses"] = new JsonObject
                {
                    ["USERNAME"] = HarviaJson.Text(parameters["USERNAME"]) ?? username,
                    ["PASSWORD_CLAIM_SECRET_BLOCK"] = secretBlock,
                    ["PASSWORD_CLAIM_SIGNATURE"] = signature,
                    ["TIMESTAMP"] = timestamp
                }
            }, "the login", cancellationToken).ConfigureAwait(false);

            if (HarviaJson.Text(answer["ChallengeName"]) is string next)
                throw new HarviaAuthenticationException($"MyHarvia's login asked for {next} (e.g. multi-factor authentication), which RIoT2 can't answer.");
            return Tokens(answer, null);
        }

        /// <summary>New tokens for a refresh token; a refused one throws <see cref="HarviaAuthenticationException"/>.</summary>
        public async Task<CognitoTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            var answer = await CallAsync("InitiateAuth", new JsonObject
            {
                ["AuthFlow"] = "REFRESH_TOKEN_AUTH",
                ["ClientId"] = _clientId,
                ["AuthParameters"] = new JsonObject { ["REFRESH_TOKEN"] = refreshToken }
            }, "the token refresh", cancellationToken).ConfigureAwait(false);
            return Tokens(answer, refreshToken);
        }

        private CognitoTokens Tokens(JsonObject answer, string? refreshToken)
        {
            var result = answer["AuthenticationResult"] as JsonObject;
            var idToken = HarviaJson.Text(result?["IdToken"]);
            if (string.IsNullOrEmpty(idToken))
                throw new HarviaUnavailableException("MyHarvia's login answer has no ID token.");
            var lifetime = HarviaJson.Number(result?["ExpiresIn"]) is double seconds && seconds > 0 ? seconds : 3600;
            return new CognitoTokens(idToken, HarviaJson.Text(result?["RefreshToken"]) ?? refreshToken, _time.GetUtcNow().AddSeconds(lifetime));
        }

        private async Task<JsonObject> CallAsync(string action, JsonObject body, string what, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url);
            request.Headers.TryAddWithoutValidation("X-Amz-Target", "AWSCognitoIdentityProviderService." + action);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1");
            using var response = await HarviaClient.SendAsync(_send, request, what, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonObject? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (response.IsSuccessStatusCode)
                return json ?? throw new HarviaUnavailableException($"MyHarvia's answer for {what} is not JSON.");

            var type = HarviaJson.Text(json?["__type"]) ?? "";
            type = type[(type.LastIndexOf('#') + 1)..];
            var message = HarviaJson.Text(json?["message"]) ?? HarviaJson.Text(json?["Message"]) ?? response.ReasonPhrase;
            var code = type.Length > 0 ? type : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (_credentialErrors.Contains(type))
                throw new HarviaAuthenticationException($"MyHarvia refused {what}: {message}");
            if (_transientErrors.Contains(type) || (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HarviaUnavailableException($"MyHarvia's login service is unavailable ({code}): {message}");
            throw new HarviaAuthenticationException($"MyHarvia's login service answered {code} for {what}: {message}");
        }

        /// <summary>
        /// The PASSWORD_CLAIM_SIGNATURE: an HMAC, keyed with the SRP session key, over the pool name, the SRP user id,
        /// the secret block and the time stamp.
        /// </summary>
        internal static string Signature(string poolName, string userId, string password, BigInteger a, BigInteger bigA, string saltHex,
            string srpBHex, string secretBlock, string timestamp)
        {
            var bigB = Parse(srpBHex);
            if ((bigB % N).IsZero)
                throw new HarviaUnavailableException("MyHarvia's login challenge is invalid (B).");
            var u = Parse(HexHash(PadHex(bigA) + PadHex(bigB)));
            if (u.IsZero)
                throw new HarviaUnavailableException("MyHarvia's login challenge is invalid (u).");

            var x = Parse(HexHash(PadHex(saltHex) + Sha256Hex(Encoding.UTF8.GetBytes($"{poolName}{userId}:{password}"))));
            // B - k * g^x may be negative; BigInteger.ModPow keeps the sign, so bring it into [0, N) first.
            var baseValue = ((bigB - K * BigInteger.ModPow(G, x, N)) % N + N) % N;
            var s = BigInteger.ModPow(baseValue, a + u * x, N);
            var key = SessionKey(s, u);

            byte[] block;
            try
            {
                block = Convert.FromBase64String(secretBlock);
            }
            catch (FormatException error)
            {
                throw new HarviaUnavailableException("MyHarvia's login challenge is invalid (secret block).", error);
            }
            var message = Encoding.UTF8.GetBytes(poolName).Concat(Encoding.UTF8.GetBytes(userId)).Concat(block).Concat(Encoding.UTF8.GetBytes(timestamp)).ToArray();
            return Convert.ToBase64String(HMACSHA256.HashData(key, message));
        }

        /// <summary>Cognito's time stamp: e.g. <c>Wed Nov 4 19:54:03 UTC 2026</c>, the day not padded.</summary>
        internal static string Timestamp(DateTimeOffset now) =>
            now.UtcDateTime.ToString("ddd MMM d HH:mm:ss 'UTC' yyyy", CultureInfo.InvariantCulture);

        private static BigInteger RandomSmallA()
        {
            while (true)
            {
                var a = new BigInteger(RandomNumberGenerator.GetBytes(128), isUnsigned: true, isBigEndian: true) % N;
                if (!a.IsZero)
                    return a;
            }
        }

        internal static BigInteger Parse(string hex) => BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        /// <summary>A non-negative number in lower-case hex without leading zeros.</summary>
        internal static string Hex(BigInteger value)
        {
            var hex = Convert.ToHexStringLower(value.ToByteArray(isUnsigned: true, isBigEndian: true)).TrimStart('0');
            return hex.Length == 0 ? "0" : hex;
        }

        internal static string PadHex(BigInteger value) => PadHex(Hex(value));

        /// <summary>Amazon's padding for hashing: an odd length gets a 0, a leading nibble of 8 or more gets 00.</summary>
        internal static string PadHex(string hex)
        {
            if (hex.Length % 2 == 1)
                return "0" + hex;
            return "89abcdefABCDEF".Contains(hex[0], StringComparison.Ordinal) ? "00" + hex : hex;
        }

        internal static string HexHash(string hex) => Sha256Hex(Convert.FromHexString(hex));

        internal static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

        /// <summary>The SRP session key: HKDF-SHA256 of the shared secret, with u as the salt.</summary>
        internal static byte[] SessionKey(BigInteger s, BigInteger u) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(PadHex(s)), 16, Convert.FromHexString(PadHex(u)), _info);
    }
}
