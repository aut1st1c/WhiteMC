using System;
using System.Reflection;

namespace WhiteMC.Core;

public static class AppVersion
{
    public static string Current { get; } = ReadCurrent();

    private static string ReadCurrent()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                // .NET 8 со SourceLink добавляет "+git-hash" — отрезаем.
                var v = info.InformationalVersion;
                var plus = v.IndexOf('+');
                return plus >= 0 ? v[..plus] : v;
            }

            var nv = asm.GetName().Version;
            return nv != null ? $"{nv.Major}.{nv.Minor}.{nv.Build}" : "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    public static int Compare(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        // Если обе — чистые числа (major.minor[.build[.rev]]) — используем System.Version.
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return va.CompareTo(vb);

        // Иначе — piecewise: числа сравниваем как числа, строки — как строки.
        var pa = a.Split('.', '-', '+');
        var pb = b.Split('.', '-', '+');
        int n = Math.Max(pa.Length, pb.Length);

        for (int i = 0; i < n; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";

            int cmp;
            if (int.TryParse(sa, out var ia) && int.TryParse(sb, out var ib))
                cmp = ia.CompareTo(ib);
            else
                cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);

            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public static bool IsRemoteNewer(string remote) => Compare(Current, remote) < 0;
}