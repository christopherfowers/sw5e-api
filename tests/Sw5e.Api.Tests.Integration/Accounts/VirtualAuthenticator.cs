using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// A software WebAuthn authenticator: it holds an ECDSA key pair per
/// credential, and it produces the attestation and assertion objects a real
/// platform authenticator would.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the passkey tests exercise the real thing. A test that
/// posts a hand-written JSON blob and asserts on the 400 that comes back proves
/// only that invalid input is invalid; it would pass just as happily against an
/// endpoint that rejected everything, including correct credentials. The only
/// way to know that passkey sign-in <em>works</em> is to present a credential
/// that verifies — which means really signing, over really the right bytes,
/// with a key the server really recorded during a real registration.
/// </para>
/// <para>
/// Everything here follows the Web Authentication specification, because the
/// verifier on the other side does. Getting any of it wrong — a flag bit, the
/// byte order of the signature counter, what exactly is hashed — produces a
/// failed assertion that looks exactly like a broken server, so the details are
/// commented with what they are rather than left as constants.
/// </para>
/// <para>
/// Because it holds the private key, it can also do what an attacker would:
/// sign for the wrong origin, replay a spent challenge, or present a credential
/// that was never registered. Those are the cases the security tests need, and
/// they are why the class exposes the ceremony in pieces rather than as one
/// happy path.
/// </para>
/// </remarks>
internal sealed class VirtualAuthenticator(string origin)
{
    /// <summary>Credentials this authenticator holds, keyed by credential ID.</summary>
    private readonly Dictionary<string, StoredCredential> _credentials = new(StringComparer.Ordinal);

    /// <summary>
    /// Answers a credential creation request, producing the JSON a browser's
    /// <c>navigator.credentials.create()</c> would return.
    /// </summary>
    /// <param name="creationOptionsJson">
    /// The options document exactly as the server sent it.
    /// </param>
    public JsonObject Create(string creationOptionsJson)
    {
        var options = JsonNode.Parse(creationOptionsJson)!.AsObject();

        var challenge = options["challenge"]!.GetValue<string>();
        var relyingPartyId = options["rp"]!["id"]?.GetValue<string>() ?? new Uri(origin).Host;
        var userHandle = Base64Url.DecodeFromChars(options["user"]!["id"]!.GetValue<string>());

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var credentialIdText = Base64Url.EncodeToString(credentialId);

        _credentials[credentialIdText] = new StoredCredential(credentialId, key, userHandle, relyingPartyId);

        var clientDataJson = BuildClientData("webauthn.create", challenge);

        // AT is set because a registration carries attested credential data;
        // the others are the state of the user's interaction with the
        // authenticator, and this one always verifies the user because the
        // server asks for userVerification "required".
        var authenticatorData = BuildAuthenticatorData(
            relyingPartyId,
            AuthenticatorFlags.UserPresent |
            AuthenticatorFlags.UserVerified |
            AuthenticatorFlags.BackupEligible |
            AuthenticatorFlags.BackedUp |
            AuthenticatorFlags.AttestedCredentialData,
            signCount: 0,
            attestedCredentialData: BuildAttestedCredentialData(credentialId, key));

        return new JsonObject
        {
            ["id"] = credentialIdText,
            ["rawId"] = credentialIdText,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientDataJson),
                ["attestationObject"] = Base64Url.EncodeToString(
                    BuildAttestationObject(authenticatorData)),
                ["transports"] = new JsonArray("internal", "hybrid"),
            },
        };
    }

    /// <summary>
    /// Answers an assertion request, producing the JSON a browser's
    /// <c>navigator.credentials.get()</c> would return.
    /// </summary>
    /// <param name="requestOptionsJson">The options document as the server sent it.</param>
    /// <param name="credentialId">
    /// Which held credential to sign with. Defaults to the only one, which is
    /// what a discoverable-credential sign-in looks like from the browser's
    /// side: it picks, the server finds out afterwards.
    /// </param>
    /// <param name="originOverride">
    /// Signs the client data for a different origin, to prove the server
    /// notices. Used only by the tests that need a credential which is
    /// cryptographically perfect and still unacceptable.
    /// </param>
    public JsonObject Get(
        string requestOptionsJson,
        string? credentialId = null,
        string? originOverride = null)
    {
        var options = JsonNode.Parse(requestOptionsJson)!.AsObject();
        var challenge = options["challenge"]!.GetValue<string>();
        var credential = _credentials[credentialId ?? _credentials.Keys.Single()];

        var relyingPartyId = options["rpId"]?.GetValue<string>() ?? credential.RelyingPartyId;

        var clientDataJson = BuildClientData("webauthn.get", challenge, originOverride ?? origin);

        // No AT flag: an assertion carries no attested credential data. The
        // counter increments on every use, which is what lets the server detect
        // a cloned authenticator replaying an old one.
        credential.SignCount++;

        var authenticatorData = BuildAuthenticatorData(
            relyingPartyId,
            AuthenticatorFlags.UserPresent |
            AuthenticatorFlags.UserVerified |
            AuthenticatorFlags.BackupEligible |
            AuthenticatorFlags.BackedUp,
            credential.SignCount,
            attestedCredentialData: null);

        // The signature covers the authenticator data followed by the SHA-256
        // of the client data — not the client data itself. That is what binds
        // one signature to one challenge and one origin, and it is the single
        // most commonly mis-implemented line in a WebAuthn client.
        var signedPayload = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signedPayload, 0);
        SHA256.HashData(clientDataJson, signedPayload.AsSpan(authenticatorData.Length));

        var signature = credential.Key.SignData(
            signedPayload,
            HashAlgorithmName.SHA256,

            // WebAuthn carries ECDSA signatures in the ASN.1 DER encoding, not
            // the fixed-width IEEE P1363 pair that .NET produces by default.
            DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = Base64Url.EncodeToString(credential.CredentialId),
            ["rawId"] = Base64Url.EncodeToString(credential.CredentialId),
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientDataJson),
                ["authenticatorData"] = Base64Url.EncodeToString(authenticatorData),
                ["signature"] = Base64Url.EncodeToString(signature),

                // How the server identifies the account when it was never told
                // who was signing in. Without it a discoverable-credential
                // sign-in has nothing to resolve.
                ["userHandle"] = Base64Url.EncodeToString(credential.UserHandle),
            },
        };
    }

    /// <summary>The credential IDs this authenticator holds.</summary>
    public IReadOnlyCollection<string> CredentialIds => _credentials.Keys;

    private byte[] BuildClientData(string type, string challenge, string? forOrigin = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = forOrigin ?? origin,
            ["crossOrigin"] = false,
        });

    /// <summary>
    /// Builds the authenticator data structure: the relying party hash, the
    /// flags, the signature counter, and — during registration only — the new
    /// credential.
    /// </summary>
    private static byte[] BuildAuthenticatorData(
        string relyingPartyId,
        AuthenticatorFlags flags,
        uint signCount,
        byte[]? attestedCredentialData)
    {
        var data = new List<byte>(37 + (attestedCredentialData?.Length ?? 0));

        // The server recomputes this hash from its own configured domain and
        // compares. It is what stops a credential minted for one site being
        // presented at another.
        data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId)));

        data.Add((byte)flags);

        // Big-endian, always: the spec says so, and the platform this runs on
        // is little-endian, so writing it the natural way would be wrong.
        Span<byte> counter = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(counter, signCount);
        data.AddRange(counter);

        if (attestedCredentialData is not null)
        {
            data.AddRange(attestedCredentialData);
        }

        return [.. data];
    }

    private static byte[] BuildAttestedCredentialData(byte[] credentialId, ECDsa key)
    {
        var data = new List<byte>();

        // All-zero AAGUID. That is what a platform authenticator reports when
        // attestation conveyance is "none", which is what this server asks for:
        // identifying the make and model of somebody's authenticator is a
        // privacy cost with no security return for a community site.
        data.AddRange(new byte[16]);

        Span<byte> length = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)credentialId.Length);
        data.AddRange(length);

        data.AddRange(credentialId);
        data.AddRange(BuildCoseKey(key));

        return [.. data];
    }

    /// <summary>
    /// Encodes the public key as a COSE_Key, which is how WebAuthn carries one.
    /// </summary>
    /// <remarks>
    /// The label order is not arbitrary. CTAP2's canonical CBOR sorts map keys
    /// by encoded length and then bytewise, which for these labels puts the
    /// positive ones (1, 3) before the negative ones (-1, -2, -3). Writing them
    /// in declaration order happens to satisfy that; writing them in any other
    /// order would produce a key some verifiers reject.
    /// </remarks>
    private static byte[] BuildCoseKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);

        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);

        writer.WriteInt32(1);   // kty
        writer.WriteInt32(2);   //   EC2

        writer.WriteInt32(3);   // alg
        writer.WriteInt32(-7);  //   ES256

        writer.WriteInt32(-1);  // crv
        writer.WriteInt32(1);   //   P-256

        writer.WriteInt32(-2);  // x
        writer.WriteByteString(parameters.Q.X!);

        writer.WriteInt32(-3);  // y
        writer.WriteByteString(parameters.Q.Y!);

        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>
    /// Wraps the authenticator data in an attestation object with the "none"
    /// format, which carries no attestation statement at all.
    /// </summary>
    private static byte[] BuildAttestationObject(byte[] authenticatorData)
    {
        // Definite-length map, canonical ordering. The keys are "fmt",
        // "attStmt" and "authData"; under CTAP2 canonical rules the shortest
        // encoded key sorts first, so "fmt" precedes "attStmt" precedes
        // "authData".
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);

        writer.WriteTextString("fmt");
        writer.WriteTextString("none");

        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();

        writer.WriteTextString("authData");
        writer.WriteByteString(authenticatorData);

        writer.WriteEndMap();

        return writer.Encode();
    }

    [Flags]
    private enum AuthenticatorFlags : byte
    {
        UserPresent = 0x01,
        UserVerified = 0x04,
        BackupEligible = 0x08,
        BackedUp = 0x10,
        AttestedCredentialData = 0x40,
    }

    private sealed record StoredCredential(
        byte[] CredentialId,
        ECDsa Key,
        byte[] UserHandle,
        string RelyingPartyId)
    {
        /// <summary>
        /// How many times this credential has signed. A real authenticator
        /// keeps this so the server can spot a clone; the server refuses an
        /// assertion whose counter has not moved forward.
        /// </summary>
        public uint SignCount { get; set; }
    }
}
