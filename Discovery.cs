using System.Net;
using Zeroconf;

namespace AirLift;

public sealed record SpeakerInfo(
    string Name,
    IPAddress Ip,
    int Port,
    bool Encrypt,
    bool AuthSetup,
    bool PasswordProtected);

public static class Discovery
{
    public static async Task<List<SpeakerInfo>> ScanAsync(TimeSpan timeout)
    {
        var hosts = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", timeout);
        var devices = new List<SpeakerInfo>();

        foreach (var host in hosts)
        {
            foreach (var service in host.Services.Values)
            {
                var ip = host.IPAddresses
                    .Select(a => IPAddress.TryParse(a, out var parsed) ? parsed : null)
                    .FirstOrDefault(a => a?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ip == null) continue;

                // RAOP service name convention: "MACADDRESS@Friendly Name"
                var raw = host.DisplayName;
                var name = raw.Contains('@') ? raw[(raw.IndexOf('@') + 1)..] : raw;

                bool supportsRsa = true, pw = false, authSetup = false;
                foreach (var props in service.Properties)
                {
                    if (props.TryGetValue("et", out var et))
                    {
                        var modes = et.Split(',');
                        supportsRsa = modes.Contains("1");
                        // et=4 (MFi/FairPlay): needs auth-setup handshake, cleartext stream
                        if (modes.Contains("4") && !supportsRsa) authSetup = true;
                    }
                    if (props.TryGetValue("pw", out var pwVal))
                        pw = pwVal.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                devices.Add(new SpeakerInfo(name, ip, service.Port,
                    Encrypt: supportsRsa && !authSetup,
                    AuthSetup: authSetup,
                    PasswordProtected: pw));
                break;
            }
        }

        return devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
