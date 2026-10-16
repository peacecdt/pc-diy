using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcWorkbench;

/// <summary>
/// OpenDB specifications and separately authorized marketplace offers. A keyword offer is
/// never evidence of an exact variant match, local stock, or compatibility.
/// </summary>
public sealed class OnlineService : IDisposable
{
    internal const string ArchiveUrl = "https://codeload.github.com/buildcores/buildcores-open-db/zip/refs/heads/main";
    const long MaxArchiveBytes = 256L * 1024 * 1024;
    const long MaxExpandedBytes = 1024L * 1024 * 1024;
    const int MaxEntryBytes = 512 * 1024;
    const int MaxEntries = 100000;
    const int MaxApiBytes = 8 * 1024 * 1024;
    readonly HttpClient client;
    readonly string cacheDirectory;
    readonly SemaphoreSlim updateLock = new(1, 1);
    readonly bool ownsClient;
    static readonly Dictionary<string, string> CategoryMap = new(StringComparer.Ordinal)
    {
        ["CPU"] = "cpu", ["GPU"] = "gpu", ["Motherboard"] = "motherboard",
        ["RAM"] = "memory", ["Storage"] = "storage", ["CPUCooler"] = "cooler",
        ["PSU"] = "psu", ["PCCase"] = "case"
    };

    public OnlineService()
    {
        client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PcWorkbench-Personal/1.0");
        cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcWorkbench", "network");
        ownsClient = true;
    }

    // Injectable transport for deterministic parser/HTTP tests; credentials are never logged.
    internal OnlineService(HttpClient transport, string cachePath)
    { client = transport; cacheDirectory = cachePath; }

    sealed class ArchiveCache
    {
        public string ETag { get; set; } = "";
        public DateTimeOffset DownloadedAt { get; set; }
    }

    public async Task<List<Part>> UpdateHardwareAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await updateLock.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            string archivePath = Path.Combine(cacheDirectory, "opendb-main.zip");
            string metadataPath = Path.Combine(cacheDirectory, "opendb-cache.json");
            ArchiveCache? cache = null;
            if (File.Exists(metadataPath) && File.Exists(archivePath))
            {
                try
                {
                    if (new FileInfo(metadataPath).Length < 8192)
                        cache = JsonSerializer.Deserialize<ArchiveCache>(await File.ReadAllTextAsync(metadataPath, ct).ConfigureAwait(false));
                }
                catch (JsonException) { progress?.Report("硬件缓存标记已损坏，将重新下载。"); }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, ArchiveUrl);
            if (cache != null && EntityTagHeaderValue.TryParse(cache.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            progress?.Report("正在检查 BuildCores OpenDB 更新（GitHub；首次约 45 MB）…");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cache != null)
            {
                progress?.Report("硬件库版本未变，正在读取已验证的本地缓存…");
                var cached = await ParseArchiveAsync(archivePath, cache.DownloadedAt, progress, timeout.Token).ConfigureAwait(false);
                ReportCatalog(cached, progress);
                return cached;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new InvalidDataException("硬件压缩包超过 256 MB 安全上限，已停止更新。");
            temporary = Path.Combine(cacheDirectory, "opendb-" + Guid.NewGuid().ToString("N") + ".tmp");
            long total = 0, lastReport = 0;
            using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                int n;
                while ((n = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxArchiveBytes) throw new InvalidDataException("硬件压缩包超过 256 MB 安全上限，已停止更新。");
                    await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                    if (total - lastReport >= 8 * 1024 * 1024)
                    { lastReport = total; progress?.Report($"已下载硬件库 {total / 1024 / 1024} MB…"); }
                }
            }
            var downloadedAt = DateTimeOffset.UtcNow;
            progress?.Report("下载完成，正在校验压缩包并导入八类硬件…");
            var parts = await ParseArchiveAsync(temporary, downloadedAt, progress, timeout.Token).ConfigureAwait(false);
            // Commit only after a complete validated parse; failed downloads preserve the old cache.
            File.Move(temporary, archivePath, true);
            temporary = null;
            string metaTemp = metadataPath + ".tmp";
            await File.WriteAllTextAsync(metaTemp, JsonSerializer.Serialize(new ArchiveCache { ETag = response.Headers.ETag?.ToString() ?? "", DownloadedAt = downloadedAt }), ct).ConfigureAwait(false);
            File.Move(metaTemp, metadataPath, true);
            ReportCatalog(parts, progress);
            return parts;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("硬件更新超过 5 分钟，已停止；原有硬件库仍可使用。请检查 GitHub 网络连接后重试。"); }
        finally
        {
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
            updateLock.Release();
        }
    }

    static void ReportCatalog(List<Part> parts, IProgress<string>? progress)
    {
        progress?.Report("硬件同步完成：" + string.Join(" / ", parts.GroupBy(p => p.Category).Select(g => Categories.All.GetValueOrDefault(g.Key, g.Key) + " " + g.Count()))
            + "。此为规格库；京东/淘宝价格与在售状态需另行查询。");
    }

    internal static async Task<List<Part>> ParseArchiveAsync(string path, DateTimeOffset fetchedAt, IProgress<string>? progress, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxArchiveBytes) throw new InvalidDataException("压缩包超过大小上限。");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("压缩包条目超过 100000 个，已拒绝。");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Length > MaxExpandedBytes - expanded) throw new InvalidDataException("压缩包声明的展开大小超过 1 GB，已拒绝。");
            expanded += entry.Length;
        }
        var result = new List<Part>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int rejected = 0, selected = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Nothing is extracted to disk; accept only a single UUID JSON in a known category.
            string[] segments = entry.FullName.Split('/');
            if (segments.Length != 4 || segments[1] != "open-db" || !CategoryMap.TryGetValue(segments[2], out string? category)
                || !segments[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(Path.GetFileNameWithoutExtension(segments[3]), out _)) continue;
            selected++;
            if (entry.Length > MaxEntryBytes) throw new InvalidDataException("单个硬件记录超过 512 KB，已拒绝该压缩包。");
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            await CopyLimitedAsync(stream, bytes, MaxEntryBytes, ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var part = MapPart(doc.RootElement, category, segments[2], segments[3], fetchedAt);
                if (part != null && seen.Add(part.Id)) result.Add(part); else rejected++;
            }
            catch (JsonException) { rejected++; }
        }
        if (CategoryMap.Values.Any(c => !result.Any(p => p.Category == c))) throw new InvalidDataException("硬件库缺少某个必要类别；保留原有数据，请稍后重试。");
        if (rejected > Math.Max(20, selected / 10)) throw new InvalidDataException("硬件库无效记录过多；保留原有数据，请稍后重试。");
        if (rejected > 0) progress?.Report($"跳过 {rejected} 条缺少名称/标识、重复或无法解析的源记录。");
        return result;
    }

    internal static Part? MapPart(JsonElement root, string category, string sourceCategory, string filename, DateTimeOffset fetchedAt)
    {
        string name = Text(root, "metadata.name"), id = Text(root, "opendb_id");
        if (string.IsNullOrWhiteSpace(name) || !Guid.TryParse(id, out var guid)
            || !string.Equals(guid.ToString(), Path.GetFileNameWithoutExtension(filename), StringComparison.OrdinalIgnoreCase)) return null;
        var part = new Part
        {
            Id = "opendb-" + guid.ToString(), Category = category, Name = name, Brand = Text(root, "metadata.manufacturer"),
            Price = null, PriceKind = "未查询国内报价", Source = "BuildCores OpenDB (CC BY-NC-SA 4.0)",
            SourceUrl = $"https://github.com/buildcores/buildcores-open-db/blob/main/open-db/{sourceCategory}/{filename}", UpdatedAt = fetchedAt
        };
        void Put(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) part.Specs[key] = value; }
        void Num(string key, string path, bool zero = false)
        {
            var n = Number(root, path);
            if (n.HasValue && (zero ? n.Value >= 0 : n.Value > 0)) Put(key, n.Value.ToString(CultureInfo.InvariantCulture));
        }
        void Forms(string key, string path) => Put(key, JoinValues(root, path, NormalizeForm));
        void Sockets(string key, string path) => Put(key, JoinValues(root, path, NormalizeSocket));
        Put("manufacturerUrl", SafeHttpUrl(Text(root, "general_product_information.manufacturer_url")));
        Put("sourceVerifiedAt", Text(root, "metadata.last_manually_spec_verified_at"));
        Put("releaseYear", Text(root, "metadata.releaseYear"));
        switch (category)
        {
            case "cpu":
                Sockets("socket", "socket"); Put("memoryType", JoinValues(root, "specifications.memory.types"));
                Num("maxMemoryGb", "specifications.memory.maxSupport");
                var ppt = Number(root, "specifications.ppt");
                if (ppt > 0) { Num("powerWatts", "specifications.ppt"); Put("powerBasis", "PPT"); }
                else if (Number(root, "specifications.tdp") > 0) { Num("powerWatts", "specifications.tdp"); Put("powerBasis", "TDP"); }
                string graphics = Text(root, "specifications.integratedGraphics.model");
                if (!string.IsNullOrWhiteSpace(graphics))
                {
                    Put("graphicsModel", graphics);
                    if (new[] { "none", "no", "n/a", "no integrated graphics", "无" }.Contains(graphics.Trim().ToLowerInvariant())) Put("igpu", "false");
                    else if (!new[] { "unknown", "tbd", "-" }.Contains(graphics.Trim().ToLowerInvariant())) Put("igpu", "true");
                }
                Num("cores", "cores.total"); Num("threads", "cores.threads");
                break;
            case "motherboard":
                Sockets("socket", "socket"); Forms("formFactor", "form_factor"); Put("memoryType", Text(root, "memory.ram_type"));
                Num("ramSlots", "memory.slots"); Num("maxMemoryGb", "memory.max"); Put("chipset", Text(root, "chipset"));
                var slots = At(root, "m2_slots");
                if (slots.ValueKind == JsonValueKind.Array && slots.EnumerateArray().All(x => new[] { "M", "B", "B+M", "E" }.Contains(Text(x, "key"))))
                    Put("m2Slots", slots.EnumerateArray().Count(x => Text(x, "key") != "E").ToString(CultureInfo.InvariantCulture));
                var sata6 = Number(root, "storage_devices.sata_6_gb_s"); var sata3 = Number(root, "storage_devices.sata_3_gb_s");
                // Null in either field means the total is unconfirmed.
                if (sata6 >= 0 && sata3 >= 0) Put("sataPorts", (sata6.Value + sata3.Value).ToString(CultureInfo.InvariantCulture));
                break;
            case "gpu":
                Num("powerWatts", "tdp"); Num("lengthMm", "length"); Num("memoryGb", "memory");
                Put("chipset", Text(root, "chipset")); Put("interface", Text(root, "interface")); Num("radiatorMm", "radiator_size");
                break;
            case "memory":
                Put("memoryType", Text(root, "ram_type")); Num("memoryGb", "capacity"); Num("memorySpeed", "speed");
                Num("modules", "modules.quantity"); Num("heightMm", "height"); Put("formFactor", Text(root, "form_factor"));
                break;
            case "storage":
                Num("storageGb", "capacity"); Put("interface", Text(root, "interface")); Put("formFactor", Text(root, "form_factor"));
                Put("storageType", Text(root, "storage_type")); break;
            case "cooler":
                Sockets("sockets", "cpu_sockets"); Num("heightMm", "height");
                var water = At(root, "water_cooled");
                if (water.ValueKind == JsonValueKind.False) Put("radiatorMm", "0");
                else if (water.ValueKind == JsonValueKind.True) Num("radiatorMm", "radiator_size");
                // OpenDB has no verified cooling wattage rating. Never infer one from dimensions.
                break;
            case "psu":
                Num("powerWatts", "wattage"); Forms("formFactor", "form_factor"); Num("lengthMm", "length");
                Put("efficiency", Text(root, "efficiency_rating")); break;
            case "case":
                Forms("supportedForms", "supported_motherboard_form_factors"); Num("maxGpuMm", "max_video_card_length");
                Num("maxCoolerMm", "max_cpu_cooler_height"); Num("maxPsuMm", "max_psu_length");
                Forms("supportedPsuForms", "supported_power_supply_form_factors");
                // Current PCCase schema has no radiator-location support data; leave unknown.
                break;
        }
        // No source benchmarks: cpuTier / gpuTier remain unknown even for familiar names.
        return part;
    }

    internal static string NormalizeSocket(string s) => s.Replace(" ", "").ToUpperInvariant();
    internal static string NormalizeForm(string s) => s.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "") switch
    {
        "MICROATX" or "MATX" => "mATX", "MINIITX" or "ITX" => "ITX",
        "EXTENDEDATX" or "EATX" => "E-ATX", "ATX" => "ATX", _ => s.Trim()
    };

    public async Task<List<Offer>> SearchOffersAsync(string keyword, AppSettings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("请先选择硬件或输入商品关键词。", nameof(keyword));
        if (keyword.Length > 200) throw new ArgumentException("商品查询关键词请控制在 200 字以内。", nameof(keyword));
        var result = new List<Offer>();
        var failures = new List<string>();
        int attempted = 0, succeeded = 0;
        async Task Run(string label, Func<Task<List<Offer>>> search)
        {
            attempted++;
            try { var offers = await search().ConfigureAwait(false); result.AddRange(offers); succeeded++; progress?.Report($"{label}返回 {offers.Count} 条关键词候选；须核对完整型号、规格、店铺及库存。"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TimeoutException or OperationCanceledException)
            { string message = $"{label}查询失败：{Redact(ex.Message, settings)}"; failures.Add(message); progress?.Report(message); }
        }
        if (!string.IsNullOrWhiteSpace(settings.JdAppKey) && !string.IsNullOrWhiteSpace(settings.JdSecret))
            await Run("京东联盟", () => SearchJdAsync(keyword, settings, ct)).ConfigureAwait(false);
        else progress?.Report("京东自动报价未启用：请在联网设置填写有商品查询权限的京东联盟 AppKey / Secret；也可使用京东搜索链接。");
        if (!string.IsNullOrWhiteSpace(settings.TaobaoAppKey) && !string.IsNullOrWhiteSpace(settings.TaobaoSecret) && !string.IsNullOrWhiteSpace(settings.TaobaoAdzoneId))
        {
            if (!long.TryParse(settings.TaobaoAdzoneId, out long adzone) || adzone <= 0)
            {
                const string error = "淘宝推广位 ID 应为 mm_xxx_xxx_数字 的最后一段正整数。";
                failures.Add(error); progress?.Report(error);
            }
            else await Run("淘宝联盟", () => SearchTaobaoAsync(keyword, settings, ct)).ConfigureAwait(false);
        }
        else progress?.Report("淘宝自动报价未启用：请填写淘宝联盟 AppKey / Secret 和推广位 ID，并取得对应 API 权限；也可使用淘宝搜索链接。");
        if (failures.Count > 0 && succeeded == 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        if (attempted == 0 && failures.Count == 0) progress?.Report("尚无平台 API 凭据：当前没有实时商品报价；网页搜索仍可使用。硬件库可独立自动更新。");
        return result;
    }

    async Task<List<Offer>> SearchJdAsync(string keyword, AppSettings settings, CancellationToken ct)
    {
        // Official JOS: https://jos.jd.com/jdunion and https://joshome.jd.com/api/detail?id=15153
        // JOS is distinct from the union platform's param_json gateway.
        var args = CommonParameters("jd.union.open.goods.query", settings.JdAppKey, "1.0");
        args["360buy_param_json"] = JsonSerializer.Serialize(new { goodsReqDTO = new { keyword, pageIndex = 1, pageSize = 30 } });
        args["sign"] = SignMd5(args, settings.JdSecret);
        string json = await PostFormAsync("https://api.jd.com/routerjson", args, ct).ConfigureAwait(false);
        return ParseJdOffers(json, DateTimeOffset.UtcNow);
    }

    async Task<List<Offer>> SearchTaobaoAsync(string keyword, AppSettings settings, CancellationToken ct)
    {
        // Official current search API: https://developer.alibaba.com/docs/api.htm?apiId=64759
        var args = CommonParameters("taobao.tbk.dg.material.optional.upgrade", settings.TaobaoAppKey, "2.0");
        args["q"] = keyword; args["adzone_id"] = settings.TaobaoAdzoneId;
        args["page_no"] = "1"; args["page_size"] = "30"; args["material_id"] = "80309";
        args["sign"] = SignMd5(args, settings.TaobaoSecret);
        string json = await PostFormAsync("https://eco.taobao.com/router/rest", args, ct).ConfigureAwait(false);
        return ParseTaobaoOffers(json, DateTimeOffset.UtcNow);
    }

    static Dictionary<string, string> CommonParameters(string method, string appKey, string version) => new(StringComparer.Ordinal)
    {
        ["method"] = method, ["app_key"] = appKey, ["format"] = "json", ["v"] = version,
        ["sign_method"] = "md5", ["timestamp"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
    };

    internal static string SignMd5(IDictionary<string, string> parameters, string secret)
    {
        var input = new StringBuilder(secret);
        foreach (var p in parameters.Where(p => p.Key != "sign" && p.Key.Length > 0 && p.Value.Length > 0).OrderBy(p => p.Key, StringComparer.Ordinal)) input.Append(p.Key).Append(p.Value);
        input.Append(secret);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }

    async Task<string> PostFormAsync(string url, Dictionary<string, string> args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(args) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxApiBytes) throw new InvalidDataException("平台响应超过 8 MB，已停止解析。");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            await CopyLimitedAsync(input, output, MaxApiBytes, timeout.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("平台在 35 秒内未完成响应，请检查网络后重试。"); }
    }

    internal static List<Offer> ParseJdOffers(string json, DateTimeOffset checkedAt)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
        var root = doc.RootElement;
        CheckError(root);
        var envelope = At(root, "jd_union_open_goods_query_responce");
        if (envelope.ValueKind == JsonValueKind.Undefined) envelope = At(root, "jd_union_open_goods_query_response");
        if (envelope.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("京东响应缺少商品查询结果，请核对 API 权限及网关。");
        CheckError(envelope);
        var data = At(envelope, "queryResult");
        if (data.ValueKind == JsonValueKind.Undefined) data = At(envelope, "result");
        using var decoded = data.ValueKind == JsonValueKind.String ? JsonDocument.Parse(data.GetString()!, new JsonDocumentOptions { MaxDepth = 48 }) : null;
        if (decoded != null) data = decoded.RootElement;
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("京东响应的 queryResult 格式无法识别。");
        string code = Text(data, "code");
        if (code != "200" && code != "0") throw new InvalidDataException($"平台错误 {code}：{FirstText(data, "message", "errorMessage", "msg")}；请核对账号权限、密钥和配额。");
        var products = At(data, "data");
        if (products.ValueKind == JsonValueKind.Object) products = At(products, "goodsResp");
        var result = new List<Offer>();
        foreach (var item in AsItems(products).Take(100))
        {
            string title = Text(item, "skuName"); if (string.IsNullOrWhiteSpace(title)) continue;
            string rawUrl = Text(item, "materialUrl");
            string url = MarketplaceUrl(rawUrl, "jd.com", "jd.hk", "3.cn");
            result.Add(new Offer
            {
                Platform = "京东", Title = title, Url = url, Price = NonNegativePrice(item, "priceInfo.price"),
                Shop = Text(item, "shopInfo.shopName"), CheckedAt = checkedAt,
                MatchStatus = (Text(item, "stockState") == "0" ? "API 提示无货 · " : "库存待核对 · ")
                    + (url.Length == 0 ? "平台未提供可用链接 · " : "") + "关键词候选，型号待核对"
            });
        }
        return result;
    }

    internal static List<Offer> ParseTaobaoOffers(string json, DateTimeOffset checkedAt)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
        var root = doc.RootElement; CheckError(root);
        var envelope = At(root, "tbk_dg_material_optional_upgrade_response");
        if (envelope.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("淘宝响应缺少升级版物料搜索结果，请核对 API 权限。");
        CheckError(envelope);
        var products = At(envelope, "result_list.map_data");
        if (products.ValueKind == JsonValueKind.Undefined && At(envelope, "result_list").ValueKind == JsonValueKind.Array) products = At(envelope, "result_list");
        var result = new List<Offer>();
        foreach (var item in AsItems(products).Take(100))
        {
            string title = Text(item, "item_basic_info.title"); if (string.IsNullOrWhiteSpace(title)) continue;
            string url = MarketplaceUrl(Text(item, "publish_info.click_url"), "taobao.com", "tmall.com", "tb.cn", "alimama.com");
            result.Add(new Offer
            {
                Platform = Text(item, "item_basic_info.user_type") == "1" ? "天猫" : "淘宝", Title = title, Url = url,
                Price = NonNegativePrice(item, "price_promotion_info.zk_final_price"),
                Shop = Text(item, "item_basic_info.shop_title"), CheckedAt = checkedAt,
                MatchStatus = (url.Length == 0 ? "未返回推广链接，需核对场景权限 · " : "") + "关键词候选，型号/库存待核对"
            });
        }
        return result;
    }

    static void CheckError(JsonElement root)
    {
        var error = At(root, "error_response");
        if (error.ValueKind == JsonValueKind.Undefined) error = At(root, "errorResponse");
        if (error.ValueKind != JsonValueKind.Undefined) throw new InvalidDataException($"平台错误 {Text(error, "code")}：{FirstText(error, "sub_msg", "msg", "zh_desc", "en_desc")}；{Text(error, "sub_code")}");
        string code = Text(root, "code");
        if (!string.IsNullOrEmpty(code) && code is not "0" and not "200")
            throw new InvalidDataException($"平台错误 {code}：{FirstText(root, "message", "errorMessage", "msg")}；请核对权限、密钥和配额。");
    }

    static string Redact(string text, AppSettings settings)
    {
        foreach (string secret in new[] { settings.JdSecret, settings.TaobaoSecret, settings.JdAppKey, settings.TaobaoAppKey })
            if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        return text.Length > 800 ? text[..800] : text;
    }

    internal static string MarketplaceUrl(string s, params string[] allowedDomains)
    {
        s = s.Trim();
        if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
        else if (!s.Contains("://", StringComparison.Ordinal) && s.Contains('.')) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0) return "";
        if (!allowedDomains.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))) return "";
        return uri.AbsoluteUri;
    }

    static string SafeHttpUrl(string s) => Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";
    static decimal? NonNegativePrice(JsonElement item, string path) { var n = Number(item, path); return n >= 0 ? n : null; }
    static string FirstText(JsonElement e, params string[] paths) => paths.Select(p => Text(e, p)).FirstOrDefault(s => s.Length > 0) ?? "未提供错误详情";
    static IEnumerable<JsonElement> AsItems(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : e.ValueKind == JsonValueKind.Object ? new[] { e } : Array.Empty<JsonElement>();
    internal static JsonElement At(JsonElement element, string path)
    {
        foreach (string segment in path.Split('.'))
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element)) return default;
        return element;
    }
    internal static string Text(JsonElement root, string path)
    {
        var element = At(root, path);
        return element.ValueKind switch
        { JsonValueKind.String => element.GetString()?.Trim() ?? "", JsonValueKind.Number => element.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => "" };
    }
    static decimal? Number(JsonElement root, string path) => decimal.TryParse(Text(root, path), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    static string JoinValues(JsonElement root, string path, Func<string, string>? normalize = null)
    {
        var element = At(root, path);
        var values = element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "") : new[] { Text(root, path) };
        return string.Join(",", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => normalize?.Invoke(x) ?? x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
    }
    static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[32768]; long total = 0; int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += n; if (total > limit) throw new InvalidDataException("网络数据或压缩记录超过安全大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }
    public void Dispose() { if (ownsClient) client.Dispose(); updateLock.Dispose(); }
}
