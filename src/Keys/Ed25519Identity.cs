using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;

namespace Dtssh.Keys;

// A generated ed25519 keypair expressed in the forms dtssh needs: the raw 32-byte
// seed (compact enough to embed in a tunnel description), the OpenSSH PEM private
// key, and the "ssh-ed25519 AAAA... comment" public line.
//
// Byte-for-byte compatible with the previous Go implementation (internal/keys):
// seeds are RFC 8032 ed25519, the public line and openssh-key-v1 marshalling match
// what Go's crypto/ed25519 + ssh-keygen produce, so tunnels/configs created by
// either tool remain interoperable. ed25519 math is provided by BouncyCastle
// (pure-managed, NativeAOT-safe); .NET has no public Ed25519 API.
internal sealed record Ed25519Identity(byte[] Seed, string PrivatePem, string PublicLine)
{
    public const int SeedSize = 32;

    public static Ed25519Identity Generate(string comment)
    {
        var seed = RandomNumberGenerator.GetBytes(SeedSize);
        return FromSeed(seed, comment);
    }

    public static Ed25519Identity FromSeed(byte[] seed, string comment)
    {
        if (seed.Length != SeedSize)
            throw new ArgumentException($"invalid ed25519 seed length {seed.Length} (want {SeedSize})");

        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        var pub = priv.GeneratePublicKey().GetEncoded(); // 32-byte public key

        return new Ed25519Identity(
            (byte[])seed.Clone(),
            MarshalOpenSshPrivateKey(seed, pub, comment),
            PublicKeyLine(pub, comment));
    }

    // Renders an ed25519 public key as an OpenSSH authorized_keys/known_hosts line.
    private static string PublicKeyLine(byte[] pub, string comment)
    {
        var blob = new List<byte>();
        blob.AddRange(SshString("ssh-ed25519"u8.ToArray()));
        blob.AddRange(SshString(pub));
        var line = "ssh-ed25519 " + Convert.ToBase64String(blob.ToArray());
        if (!string.IsNullOrEmpty(comment)) line += " " + comment;
        return line;
    }

    // Encodes an unencrypted ed25519 key in the "openssh-key-v1" private key format
    // (the same bytes ssh-keygen emits), so ssh/sshd accept the reconstructed key.
    private static string MarshalOpenSshPrivateKey(byte[] seed, byte[] pub, string comment)
    {
        var pubWire = new List<byte>();
        pubWire.AddRange(SshString("ssh-ed25519"u8.ToArray()));
        pubWire.AddRange(SshString(pub));

        // OpenSSH stores the private key as seed||pub (64 bytes).
        var priv64 = new byte[64];
        Buffer.BlockCopy(seed, 0, priv64, 0, 32);
        Buffer.BlockCopy(pub, 0, priv64, 32, 32);

        var check = RandomNumberGenerator.GetBytes(4);
        var sec = new List<byte>();
        sec.AddRange(check);
        sec.AddRange(check);
        sec.AddRange(SshString("ssh-ed25519"u8.ToArray()));
        sec.AddRange(SshString(pub));
        sec.AddRange(SshString(priv64));
        sec.AddRange(SshString(System.Text.Encoding.UTF8.GetBytes(comment)));
        for (byte i = 1; sec.Count % 8 != 0; i++) sec.Add(i);

        var blob = new List<byte>();
        blob.AddRange("openssh-key-v1\0"u8.ToArray());
        blob.AddRange(SshString("none"u8.ToArray()));       // ciphername
        blob.AddRange(SshString("none"u8.ToArray()));       // kdfname
        blob.AddRange(SshString(Array.Empty<byte>()));      // kdfoptions
        blob.AddRange(U32(1));                               // number of keys
        blob.AddRange(SshString(pubWire.ToArray()));         // public key
        blob.AddRange(SshString(sec.ToArray()));             // private section

        return PemEncode("OPENSSH PRIVATE KEY", blob.ToArray());
    }

    // Encodes a seed for compact transport (raw-URL base64, no padding).
    public static string SeedB64(byte[] seed) => Base64Url.Encode(seed);

    // Parses a seed produced by SeedB64.
    public static byte[] DecodeSeedB64(string s) => Base64Url.Decode(s.Trim());

    private static byte[] SshString(byte[] b)
    {
        var outb = new byte[4 + b.Length];
        BinaryPrimitives.WriteUInt32BigEndian(outb, (uint)b.Length);
        Buffer.BlockCopy(b, 0, outb, 4, b.Length);
        return outb;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    // PEM with 64-char base64 lines, matching Go's encoding/pem output.
    private static string PemEncode(string type, byte[] der)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("-----BEGIN ").Append(type).Append("-----\n");
        var b64 = Convert.ToBase64String(der);
        for (int i = 0; i < b64.Length; i += 64)
            sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        sb.Append("-----END ").Append(type).Append("-----\n");
        return sb.ToString();
    }
}

// Raw-URL base64 (no padding), matching Go's base64.RawURLEncoding.
internal static class Base64Url
{
    public static string Encode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
