using System.Security.Cryptography;
using System.Text;

namespace Hawkynt.CloudStorage.OAuth;

/// <summary>
/// A PKCE (RFC 7636) verifier/challenge pair for the OAuth2 installed-application flow.
/// The verifier is a high-entropy secret kept locally; the challenge is its SHA-256 digest
/// sent on the authorization request, so an intercepted authorization code is useless
/// without the verifier.
/// </summary>
public sealed record PkcePair(string Verifier, string Challenge) {

  public const string ChallengeMethod = "S256";

  /// <summary>Generates a fresh pair: a 32-byte random verifier and its S256 challenge, both base64url-encoded.</summary>
  public static PkcePair Create() {
    var verifierBytes = RandomNumberGenerator.GetBytes(32);
    var verifier = Base64Url.Encode(verifierBytes);
    var challenge = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    return new(verifier, challenge);
  }

}

/// <summary>Base64url (RFC 4648 §5) without padding — the encoding OAuth2/JWT use for opaque values.</summary>
public static class Base64Url {

  public static string Encode(ReadOnlySpan<byte> bytes)
    => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}
