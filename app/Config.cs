using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rplus;

/// <summary>
/// Persisted settings and identity. Uses the standard remote-desktop config keys
/// (see MECHANICS.md): id, one-time/permanent password, approve/verification mode,
/// per-session permissions, image quality, view style and the server fields.
/// </summary>
internal sealed class Config
{
    public string Id { get; set; } = "";
    public string PermanentPassword { get; set; } = "";
    public bool UsePermanentPassword { get; set; } = false;     // verification-method
    public int TemporaryPasswordLength { get; set; } = 6;       // 6 or 8
    public bool NumericOneTimePassword { get; set; } = false;   // allow-numeric-one-time-password
    public string ApproveMode { get; set; } = "both";           // password | click | both

    public bool EnableKeyboard { get; set; } = true;
    public bool EnableClipboard { get; set; } = true;
    public bool EnableFileTransfer { get; set; } = true;
    public bool EnableAudio { get; set; } = true;

    // Option defaults (MECHANICS.md).
    public string ImageQuality { get; set; } = "Balanced";      // balanced (default) | best | low | custom
    public int CustomQuality { get; set; } = CustomQualityDefault;
    public string ViewStyle { get; set; } = "Original";         // original (default) | adaptive ("Adjust window")
    public int CustomFps { get; set; } = FpsDefault;            // custom-fps 30, 5..120

    public const int CustomQualityDefault = 50, CustomQualityMin = 10, CustomQualityMax = 2000;
    public const int CustomQualityValidMax = 4095;   // the option validator's upper bound
    public const int FpsDefault = 30, FpsMin = 5, FpsMax = 120;

    // Bitrate ratios per quality setting
    public const float RatioBest = 1.5f, RatioBalanced = 0.67f, RatioLow = 0.5f;
    public const float RatioMin = 0.2f, RatioMax = 40f;

    /// <summary>The quality ratio: best 1.5, balanced 0.67, low 0.5,
    /// custom = clamp(2 × quality / 100, 0.2, 40).</summary>
    public float QualityRatio() => ImageQuality switch
    {
        "Best" => RatioBest,
        "Low" => RatioLow,
        "Custom" => Math.Clamp(2f * Math.Clamp(CustomQuality, CustomQualityMin, CustomQualityValidMax) / 100f, RatioMin, RatioMax),
        _ => RatioBalanced,
    };

    /// <summary>Rplus sends JPEG frames rather than a video stream, so the ratio is turned into a
    /// JPEG quality: 30 + 40 × ratio, kept within 10..95 (Rplus's own mapping).</summary>
    public int JpegQuality() => Math.Clamp((int)Math.Round(30 + 40 * QualityRatio()), 10, 95);

    public int Fps() => Math.Clamp(CustomFps, FpsMin, FpsMax);

    public int DirectPort { get; set; } = Rv.DirectPort;        // direct-access-port

    /// <summary>The saved port if it is above 0, otherwise 21118.</summary>
    public int ListenPort() => DirectPort > 0 ? DirectPort : Rv.DirectPort;
    public string RelayServer { get; set; } = "";
    public string IdServer { get; set; } = "";
    public string ServerKey { get; set; } = "";                 // "Key" of the ID/relay server
    public string Uuid { get; set; } = "";                      // stable registration uuid (base64)
    public string Pk { get; set; } = "";                        // registration public-key bytes (base64)
    public bool UseIpWhitelist { get; set; } = false;
    public string Whitelist { get; set; } = "";

    public List<Peer> Peers { get; set; } = new();

    [JsonIgnore] public string RuntimeOneTimePassword { get; set; } = "";

    // ---- charsets (MECHANICS.md) ----
    private const string AlnumCharset = "23456789abcdefghijkmnpqrstuvwxyz";
    private const string NumCharset   = "0123456789";

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rplus");
    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (c != null) { c.EnsureId(); return c; }
            }
        }
        catch { /* fall through to fresh config */ }
        var fresh = new Config();
        fresh.EnsureId();
        fresh.RuntimeOneTimePassword = fresh.NewOneTimePassword();
        fresh.Save();
        return fresh;
    }

    [JsonIgnore] public bool Ephemeral { get; set; }   // test configs are never written

    public static Config Temporary(string idServer, string relay, string key)
    {
        var c = new Config { Ephemeral = true, IdServer = idServer, RelayServer = relay, ServerKey = key, ApproveMode = "password" };
        c.Id = RandomId();   // two test configs on one PC must not share the MAC-derived ID
        c.RuntimeOneTimePassword = c.NewOneTimePassword();
        return c;
    }

    /// <summary>Settings that are never saved: a random ID, no ID/relay server, default approval.</summary>
    public static Config Demo()
    {
        var c = new Config { Ephemeral = true };
        c.Id = RandomId();
        c.RuntimeOneTimePassword = c.NewOneTimePassword();
        return c;
    }

    public void Save()
    {
        if (Ephemeral) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best effort */ }
    }

    /// <summary>The device ID, made once and then kept:
    /// the last four bytes of the network card's MAC address with the top three bits cleared
    /// ((mac[2] &amp; 0x1F) &lt;&lt; 24 | mac[3] &lt;&lt; 16 | mac[4] &lt;&lt; 8 | mac[5]) as a decimal number.
    /// Without a usable MAC address it falls back to a random 9-digit number.</summary>
    private void EnsureId()
    {
        if (!string.IsNullOrEmpty(Id)) return;
        Id = MacId() ?? RandomId();
    }

    public const uint IdMask = 536870911;   // 0x1FFFFFFF: top three bits of mac[2] cleared

    public static string MacId()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length != 6 || mac.All(b => b == 0)) continue;
                uint id = (((uint)mac[2] << 24) | ((uint)mac[3] << 16) | ((uint)mac[4] << 8) | mac[5]) & IdMask;
                var s = id.ToString();
                if (s.Length >= 6) return s;    // shorter IDs aren't accepted by the server (6..16)
            }
        }
        catch { }
        return null;
    }

    public static string RandomId()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append((char)('1' + RandomNumberGenerator.GetInt32(9)));
        for (int i = 0; i < 8; i++) sb.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        return sb.ToString();
    }

    /// <summary>The server said this ID belongs to another machine: pick a random one.</summary>
    public void RegenerateId() { Id = RandomId(); }

    /// <summary>Groups an ID in threes for display ("123 456 789").</summary>
    public static string FormatId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsDigit)) return id;
        var parts = new List<string>();
        for (int i = 0; i < id.Length; i += 3) parts.Add(id.Substring(i, Math.Min(3, id.Length - i)));
        return string.Join(" ", parts);
    }

    public byte[] MachineUuid()
    {
        if (string.IsNullOrEmpty(Uuid)) { Uuid = Convert.ToBase64String(Guid.NewGuid().ToByteArray()); Save(); }
        return Convert.FromBase64String(Uuid);
    }

    public byte[] MachinePk()
    {
        if (string.IsNullOrEmpty(Pk)) { Pk = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); Save(); }
        return Convert.FromBase64String(Pk);
    }

    /// <summary>A new one-time password of the configured length and charset.</summary>
    public string NewOneTimePassword()
    {
        string set = NumericOneTimePassword ? NumCharset : AlnumCharset;
        // "8" → 8, "10" → 10, anything else → 6
        int len = TemporaryPasswordLength is 8 or 10 ? TemporaryPasswordLength : 6;
        var sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(set[RandomNumberGenerator.GetInt32(set.Length)]);
        return sb.ToString();
    }

    /// <summary>Which password(s) a connecting peer may use, per verification-method.</summary>
    public bool PasswordAccepted(string entered)
    {
        if (string.IsNullOrEmpty(entered)) return false;
        bool perm = !string.IsNullOrEmpty(PermanentPassword) && entered == PermanentPassword;
        bool once = !string.IsNullOrEmpty(RuntimeOneTimePassword) && entered == RuntimeOneTimePassword;
        if (UsePermanentPassword) return perm;        // use-permanent-password
        return perm || once;                          // use-both-passwords (default)
    }

    public bool RequiresClickApproval => ApproveMode is "click" or "both";
    public bool RequiresPassword      => ApproveMode is "password" or "both";
}

internal sealed class Peer
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Host { get; set; } = "";       // IP / hostname for direct connect
    public string Platform { get; set; } = "win"; // win|mac|linux|android
    public bool Favorite { get; set; } = false;
    public string LastConnected { get; set; } = "";
}
