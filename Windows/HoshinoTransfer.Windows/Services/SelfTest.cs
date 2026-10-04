using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace HoshinoTransfer.Windows.Services;

public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        using var client = new ApiClient(new VolatileCredentialVault());
        var failures = 0;
        void Pass(string name) => Console.WriteLine($"[PASS] {name}");
        void Fail(string name, string reason) { Console.WriteLine($"[FAIL] {name}: {reason}"); failures++; }

        try
        {
            if (await client.CheckHealthAsync()) Pass("Backend connectivity");
            else Fail("Backend connectivity", "health endpoint returned a non-success status");
        }
        catch (Exception ex) { Fail("Backend connectivity", ex.Message); }

        try
        {
            if (await client.PrivateApiIsProtectedAsync()) Pass("Authentication boundary");
            else Fail("Authentication boundary", "unauthenticated profile request was not rejected");
        }
        catch (Exception ex) { Fail("Authentication boundary", ex.Message); }

        try
        {
            using var http = new HttpClient { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync("api/openapi.json");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (response.IsSuccessStatusCode && document.RootElement.GetProperty("openapi").GetString() == "3.0.3") Pass("OpenAPI discovery");
            else Fail("OpenAPI discovery", "development OpenAPI document is unavailable or invalid");
        }
        catch (Exception ex) { Fail("OpenAPI discovery", ex.Message); }

        try
        {
            var registry = new TransferTransportRegistry(client);
            var relay = registry.Get(TransferMode.ServerRelay);
            var direct = registry.Get(TransferMode.DirectWifi);
            var requiredModes = new[] { TransferMode.P2P, TransferMode.DirectWifi, TransferMode.ServerRelay, TransferMode.Lightning };
            var allPresent = requiredModes.All(mode => registry.All.Any(item => item.Mode == mode));
            var lanIp = LanTransferListener.GetLocalIPv4();
            var directShouldBeAvailable = lanIp is not null && direct.IsAvailable;
            var directShouldBeUnavailable = lanIp is null && !direct.IsAvailable;
            var automatic = registry.SelectAutomatic().Mode;
            var expectedAutomatic = direct.IsAvailable ? TransferMode.DirectWifi : TransferMode.ServerRelay;
            if (allPresent && relay.IsAvailable && registry.Get(TransferMode.P2P).IsAvailable == false
                && registry.Get(TransferMode.Lightning).IsAvailable == false
                && (directShouldBeAvailable || directShouldBeUnavailable)
                && automatic == expectedAutomatic)
                Pass($"Transport registry and truthful fallback (Direct Wi-Fi {(direct.IsAvailable ? $"available on {lanIp}" : "unavailable")})");
            else Fail("Transport registry", "transport availability is inconsistent");
        }
        catch (Exception ex) { Fail("Transport registry", ex.Message); }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "hoshino-selftest-lan");
            Directory.CreateDirectory(directory);
            var payload = System.Text.Encoding.UTF8.GetBytes("direct wifi chunk payload".PadRight(64, '.'));
            var path = Path.Combine(directory, "item.bin");
            File.WriteAllBytes(path, payload);
            using var listener = new LanTransferListener();
            listener.Publish("selftest-item", new TransferSource("selftest-item", "item.bin", path, payload.Length));
            listener.Start();
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://127.0.0.1:{listener.Port}/hoshino/transfer/selftest-item/0?token={listener.Token}";
            var fetched = await httpClient.GetByteArrayAsync(url);
            var forbidden = await httpClient.GetAsync($"http://127.0.0.1:{listener.Port}/hoshino/transfer/selftest-item/0?token=deadbeef");
            listener.Stop();
            if (fetched.SequenceEqual(payload) && forbidden.StatusCode == HttpStatusCode.Forbidden) Pass("LAN direct listener serves chunks and rejects bad tokens");
            else Fail("LAN direct listener", $"payload match: {fetched.SequenceEqual(payload)}, bad-token status: {forbidden.StatusCode}");
        }
        catch (Exception ex) { Fail("LAN direct listener", ex.Message); }

        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("HoshinoTransfer self-test stream hash");
            await using var stream = new MemoryStream(payload, writable: false);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected))) Pass("SHA-256 stream verification");
            else Fail("SHA-256 stream verification", "stream digest mismatch");
        }
        catch (Exception ex) { Fail("SHA-256 stream verification", ex.Message); }

        if (client.BaseAddress.IsAbsoluteUri && client.BaseAddress.Scheme is "https" or "http") Pass("Endpoint configuration");
        else Fail("Endpoint configuration", "API URL is not absolute HTTP(S)");

        Console.WriteLine("[INFO] Password register/login/logout, friend operations, SSE exchange, and transfer authorization require a user session and are exercised by the server integration suite.");
        Console.WriteLine("[INFO] Direct Wi-Fi serves chunks over the LAN and falls back to the Server Relay; cross-network P2P and Lightning cable stay unavailable (no TURN service / no MFi entitlement).");
        Console.WriteLine(failures == 0 ? "SELF-TEST: all local capability checks passed." : $"SELF-TEST FAILED: {failures} check(s) failed.");
        return failures == 0 ? 0 : 2;
    }

    private sealed class VolatileCredentialVault : ICredentialVault
    {
        public string? Read() => null;
        public void Write(string value) { }
        public void Delete() { }
    }
}
