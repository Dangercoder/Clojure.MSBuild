using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace BareMetal;

/// C# lives in the same project as the Clojure — one csproj, one build.
/// Clojure calls these like any static method.
public static class Native
{
    // zlib ships with the OS. No wrapper library, no bindings package — one attribute.
    [DllImport("z", EntryPoint = "crc32")]
    public static extern uint Crc32(uint crc, byte[] buf, uint len);

    [DllImport("libSystem", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, byte[]? oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    /// Raw macOS syscall: ask the kernel what CPU this is.
    public static string CpuBrand()
    {
        nuint len = 0;
        SysctlByName("machdep.cpu.brand_string", null, ref len, IntPtr.Zero, 0);
        var buf = new byte[len];
        SysctlByName("machdep.cpu.brand_string", buf, ref len, IntPtr.Zero, 0);
        return System.Text.Encoding.UTF8.GetString(buf, 0, (int)len - 1);
    }
}

/// Baselines for the benchmark ladder — the "how fast could it possibly go" rungs.
public static class Baseline
{
    public static double ScalarDot(float[] xs, float[] ys)
    {
        double acc = 0;
        for (int i = 0; i < xs.Length; i++) acc += (double)xs[i] * ys[i];
        return acc;
    }

    public static double SimdDot(float[] xs, float[] ys)
    {
        var acc = Vector<float>.Zero;
        int vn = Vector<float>.Count, i = 0;
        for (; i + vn <= xs.Length; i += vn)
            acc += new Vector<float>(xs, i) * new Vector<float>(ys, i);
        double tail = 0;
        for (; i < xs.Length; i++) tail += (double)xs[i] * ys[i];
        return Vector.Dot(acc, Vector<float>.One) + tail;
    }
}
