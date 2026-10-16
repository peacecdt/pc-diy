using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PcWorkbench;
public static class StorageService
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcWorkbench");
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "Data");
    public static string? LastError { get; private set; }
    public static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, AppJson.Options), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            if (Path.GetDirectoryName(path) == DataDir)
            {
                using var embedded = typeof(StorageService).Assembly.GetManifestResourceStream("PcWorkbench.Data." + Path.GetFileName(path));
                if (embedded != null) return JsonSerializer.Deserialize<T>(embedded, AppJson.Options);
            }
            return default;
        }
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppJson.Options); }
        catch (Exception ex) { LastError = $"无法读取 {Path.GetFileName(path)}：{ex.Message}。原文件已保留。"; return default; }
    }
    public static List<Part> LoadCatalog()
    {
        var seed = Read<List<Part>>(Path.Combine(DataDir, "seed.json")) ?? new();
        var cached = Read<List<Part>>(Path.Combine(Root, "catalog.json")) ?? new();
        return seed.Concat(cached).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name) && Categories.All.ContainsKey(p.Category))
            .GroupBy(p => p.Id).Select(g => g.Last()).OrderBy(p => p.Brand).ThenBy(p => p.Name).ToList();
    }
    public static List<BuildPlan> LoadPresets() => Read<List<BuildPlan>>(Path.Combine(DataDir, "presets.json")) ?? new();
    public static AppSettings LoadSettings()
    {
        string path = Path.Combine(Root, "settings.dat");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(Vault.Unprotect(File.ReadAllBytes(path)), AppJson.Options) ?? new(); }
        catch (Exception ex) { LastError = "设置读取失败，已使用默认设置：" + ex.Message; return new(); }
    }
    public static void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "settings.dat");
        File.WriteAllBytes(path + ".tmp", Vault.Protect(JsonSerializer.SerializeToUtf8Bytes(settings, AppJson.Options)));
        File.Move(path + ".tmp", path, true);
    }
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, AppJson.Options), AppJson.Options)!;
}
internal static class Vault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    public static byte[] Protect(byte[] data) => Transform(data, true);
    public static byte[] Unprotect(byte[] data) => Transform(data, false);
    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref input, "PcWorkbench", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
