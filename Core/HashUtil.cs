using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class HashUtil
{
    public static async Task<string> ComputeAsync(string path, string algo, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);

        if (algo == "sha512")
        {
            using var h = SHA512.Create();
            return Convert.ToHexString(await h.ComputeHashAsync(fs, ct)).ToLowerInvariant();
        }
        if (algo == "sha1")
        {
            using var h = SHA1.Create();
            return Convert.ToHexString(await h.ComputeHashAsync(fs, ct)).ToLowerInvariant();
        }
        if (algo == "sha256")
        {
            using var h = SHA256.Create();
            return Convert.ToHexString(await h.ComputeHashAsync(fs, ct)).ToLowerInvariant();
        }

        throw new ArgumentException($"Неизвестный алгоритм: {algo}");
    }
}