using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class Http
{
    public static HttpClient Client { get; }

    static Http()
    {
        Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = System.TimeSpan.FromSeconds(120)
        };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        => Encoding.UTF8.GetString(await GetBytesAsync(url, ct));
}