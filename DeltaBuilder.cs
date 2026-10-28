// Build-time COPY/LITERAL delta generator. No game data is compiled into this tool.
using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;

class DeltaBuilder
{
    static ulong Key(byte[] b, int p)
    {
        return BitConverter.ToUInt64(b, p);
    }

    static int Main(string[] a)
    {
        try
        {
            Build(a[0], a[1], a[2]);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    static void Build(string source, string target, string output)
    {
        byte[] s = File.ReadAllBytes(source), t = File.ReadAllBytes(target);
        var index = new Dictionary<ulong, int>();
        for (int i = 0; i + 16 <= s.Length; i += 32)
        {
            ulong k = Key(s, i);
            if (!index.ContainsKey(k))
                index.Add(k, i);
        }

        long copied = 0, literals = 0;
        int p = 0, lit = 0;
        using (var f = File.Create(output))
        using (var gz = new GZipStream(f, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("EVDELTA1"));
            w.Write((long)t.Length);
            while (p < t.Length)
            {
                int from = 0, n = 0;
                if (p + 16 <= t.Length && index.TryGetValue(Key(t, p), out from) && Key(t, p + 8) == Key(s, from + 8))
                {
                    n = 16;
                    while (p + n < t.Length && from + n < s.Length && t[p + n] == s[from + n])
                        n++;
                }

                if (n >= 32)
                {
                    if (p > lit)
                    {
                        w.Write((byte)1);
                        w.Write(p - lit);
                        w.Write(t, lit, p - lit);
                        literals += p - lit;
                    }

                    w.Write((byte)0);
                    w.Write((long)from);
                    w.Write(n);
                    copied += n;
                    p += n;
                    lit = p;
                }
                else
                    p++;
            }

            if (p > lit)
            {
                w.Write((byte)1);
                w.Write(p - lit);
                w.Write(t, lit, p - lit);
                literals += p - lit;
            }
        }

        Console.WriteLine("copy=" + copied + " literal=" + literals + " delta=" + new FileInfo(output).Length);
    }
}
