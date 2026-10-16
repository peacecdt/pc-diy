using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PcWorkbench;
public partial class MainWindow : Window
{
    private List<Part> _catalog = new();
    private List<BuildPlan> _presets = new();
    private BuildPlan _plan = new();
    private AppSettings _settings = new();
    private readonly OnlineService _online = new();
    private CancellationTokenSource? _cts;
    private readonly List<string> _logs = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string _category = "cpu";
    private Part? _detailPart;
    private bool _ready, _metaLoading, _busy;
    private readonly string[] _args;
    private bool IsSmoke => _args.Contains("--smoke");
    public MainWindow(string[] args)
    {
        _args = args;
        InitializeComponent();
        MinWidth = 1100; MinHeight = 660;
        Width = Math.Max(MinWidth, Math.Min(1480, SystemParameters.WorkArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(940, SystemParameters.WorkArea.Height - 32));
        if (_args.Contains("--compact")) { Width = 1180; Height = 700; }
        _settings = StorageService.LoadSettings();
        _catalog = StorageService.LoadCatalog();
        _presets = StorageService.LoadPresets();
        _plan = IsSmoke ? (_presets.ElementAtOrDefault(2) ?? _presets.FirstOrDefault() ?? new())
            : StorageService.Read<BuildPlan>(Path.Combine(StorageService.Root, "autosave.json")) ?? _presets.ElementAtOrDefault(2) ?? new();
        _plan = StorageService.Clone(_plan);
        NormalizePlan(_plan);
        PresetBox.ItemsSource = new[] { new BuildPlan { Name = "加载示例方案…" } }.Concat(_presets).ToList();
        PresetBox.SelectedIndex = 0; SortBox.SelectedIndex = 0;
        AutoCheck.IsChecked = _settings.AutoUpdate;
        IntervalBox.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 6, 12, 24, 48 }, _settings.UpdateHours));
        JdKeyBox.Text = _settings.JdAppKey; JdSecretBox.Password = _settings.JdSecret;
        TbKeyBox.Text = _settings.TaobaoAppKey; TbSecretBox.Password = _settings.TaobaoSecret; TbAdzoneBox.Text = _settings.TaobaoAdzoneId;
        _ready = true; SetMeta(); RefreshAll(); Filter(); UpdateSyncInfo();
        SizeChanged += (_, _) => UpdateResponsive();
        Log($"已载入 {_catalog.Count:N0} 个硬件型号。双击型号可加入配置；价格待你录入或关联核验后的商品。");
        if (StorageService.LastError is not null) Log(StorageService.LastError);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Filter(); };
        _timer.Tick += async (_, _) => await AutoSync();
        Loaded += async (_, _) =>
        {
            UpdateResponsive();
            if (IsSmoke) { await RunSmoke(); return; }
            _timer.Start(); await Task.Delay(2500); if (IsVisible) await AutoSync();
        };
        Closing += (_, _) => { _timer.Stop(); _cts?.Cancel(); AutoSave(); _online.Dispose(); };
    }
    private static void NormalizePlan(BuildPlan p)
    {
        p.Parts ??= new(); p.Name ??= "我的配置"; p.Usage ??= "2K 游戏";
        foreach (var k in p.Parts.Keys.ToList())
        {
            var part = p.Parts[k];
            if (!Categories.All.ContainsKey(k) || part is null || part.Category != k || string.IsNullOrWhiteSpace(part.Name)) { p.Parts.Remove(k); continue; }
            part.Specs ??= new(); part.Offers ??= new();
            if (part.Price < 0) part.Price = null;
        }
        if (p.Budget < 0) p.Budget = 0;
    }
    private void SetMeta()
    {
        _metaLoading = true;
        PlanName.Text = _plan.Name; BudgetBox.Text = _plan.Budget.ToString("0.##", CultureInfo.InvariantCulture);
        UsageBox.SelectedItem = UsageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == _plan.Usage) ?? UsageBox.Items[2];
        _metaLoading = false;
    }
    private void RefreshAll()
    {
        if (!_ready) return;
        RenderBuild(); RenderRating();
        var priceCount = _plan.Parts.Values.Count(p => p.Price.HasValue);
        TotalText.Text = "¥ " + _plan.Parts.Values.Sum(p => p.Price ?? 0).ToString("N0");
        PriceCoverage.Text = $"{priceCount} / {_plan.Parts.Count} 项已报价 · 缺价不计入";
        CatalogCount.Text = _catalog.Count.ToString("N0");
        AutoSave();
    }
    private void RenderBuild()
    {
        BuildRows.Children.Clear();
        foreach (var (key, name) in Categories.All)
        {
            _plan.Parts.TryGetValue(key, out var p);
            var body = new StackPanel();
            var top = new DockPanel();
            top.Children.Add(new TextBlock { Text = name, Foreground = Brush("#68767F"), FontSize = 11 });
            top.Children.Add(new TextBlock { Text = p?.Price is decimal price ? $"¥ {price:N0}" : p == null ? "+" : "未报价", HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush("#187A64"), FontSize = 11 });
            body.Children.Add(top);
            body.Children.Add(new TextBlock { Text = p?.Name ?? "点击选择" , Margin = new Thickness(0, 6, 0, 0), MaxHeight = 38, FontSize = 12 });
            var choose = new Button { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10), Background = Brush(_category == key ? "#EDF6F1" : "#FAFBFA"), BorderBrush = Brush(_category == key ? "#187A64" : "#E7ECE9"), Margin = new Thickness(0, 0, 0, 7) };
            // ContentPresenter centers by default; the fixed inner width gives consistent alignment.
            body.Width = 218;
            choose.Click += (_, _) =>
            {
                _category = key; SearchBox.Text = ""; Filter(); RenderBuild();
                var match = PartsGrid.Items.OfType<PartRow>().FirstOrDefault(r => r.Part.Id == p?.Id);
                PartsGrid.SelectedItem = match;
                if (match != null) PartsGrid.ScrollIntoView(match);
                if (p != null) ShowPart(p);
            };
            if (p != null)
            {
                var menu = new ContextMenu();
                var quote = new MenuItem { Header = "录入价格 / 查看商品" }; quote.Click += (_, _) => ShowOffers(p); menu.Items.Add(quote);
                var remove = new MenuItem { Header = "移除此配件" }; remove.Click += (_, _) => { _plan.Parts.Remove(key); RefreshAll(); }; menu.Items.Add(remove);
                choose.ContextMenu = menu; choose.ToolTip = "点击查看；右键录入价格或移除";
            }
            BuildRows.Children.Add(choose);
        }
    }
    private void RenderRating()
    {
        var r = RatingEngine.Evaluate(_plan);
        GradeText.Text = r.Grade; ScoreText.Text = $"{r.Score} / 100";
        GradeText.Foreground = Brush(r.Grade == "差" ? "#B44C3B" : r.Grade == "优秀" ? "#187A64" : "#98722B");
        GradeCaption.Text = "规则完整性与搭配评分 · 非跑分 / 非帧率预测";
        PowerText.Text = r.EstimatedWatts > 0 ? $"约 {r.EstimatedWatts} W" : "关键功耗待核验";
        PsuText.Text = r.RecommendedPsu > 0 ? $"经验建议 ≥ {r.RecommendedPsu} W 电源，需核对供电接口" : "补齐功耗参数后再评估电源";
        FindingRows.Children.Clear();
        foreach (var f in r.Findings)
        {
            string color = f.Level switch { "error" => "#AE4537", "warn" => "#9B7426", "good" => "#187A64", _ => "#64727B" };
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            stack.Children.Add(new TextBlock { Text = (f.Level switch { "error" => "×  ", "warn" => "!  ", "good" => "✓  ", _ => "·  " }) + f.Title, FontWeight = FontWeights.SemiBold, Foreground = Brush(color), FontSize = 12 });
            stack.Children.Add(new TextBlock { Text = f.Detail, FontSize = 11, Foreground = Brush("#5D6F65"), Margin = new Thickness(15, 4, 0, 0) });
            FindingRows.Children.Add(stack);
        }
    }
    private void Filter()
    {
        if (!_ready) return;
        var words = SearchAliases(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<Part> query = _catalog.Where(p => p.Category == _category && words.All(w => (p.Name + " " + p.Brand + " " + string.Join(" ", p.Specs.Values)).Contains(w, StringComparison.OrdinalIgnoreCase)));
        query = SortBox.SelectedIndex switch { 1 => query.OrderBy(p => p.Price ?? decimal.MaxValue).ThenBy(p => p.Name), 2 => query.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Name), _ => query.OrderBy(p => p.Brand).ThenBy(p => p.Name) };
        var rows = query.Select(p => new PartRow(p)).ToList();
        PartsGrid.ItemsSource = rows;
        CategoryTitle.Text = "挑选" + Categories.All[_category]; ResultCount.Text = $"{rows.Count:N0} 个型号";
        if (rows.Count > 0)
        {
            var current = words.Length == 0 && _plan.Parts.TryGetValue(_category, out var chosen) ? rows.FirstOrDefault(r => r.Part.Id == chosen.Id) : null;
            PartsGrid.SelectedItem = current ?? rows[0];
            if (current != null) PartsGrid.ScrollIntoView(current);
        }
        else { _detailPart = null; SelectedName.Text = "没有找到匹配型号"; SelectedDetail.Text = "尝试缩短关键词，或在联网与数据页同步硬件库。"; AddButton.IsEnabled = false; }
    }
    private void ShowPart(Part p)
    {
        _detailPart = p; AddButton.IsEnabled = true; SelectedName.Text = p.Name;
        SelectedDetail.Text = PartRow.Describe(p) + "\n" + p.Source + " · " + (p.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "时间未提供");
        SelectedName.ToolTip = p.Name + "\n" + SelectedDetail.Text;
    }
    private void UpdateResponsive()
    {
        bool compact = ActualHeight < 830;
        HeroRow.Height = new GridLength(compact ? 72 : 100);
        SelectedDetail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        JdPurchaseButton.Content = compact ? "京东" : "京东搜同款";
        TbPurchaseButton.Content = compact ? "淘宝" : "淘宝搜同款";
    }
    private static string SearchAliases(string text)
    {
        var aliases = new Dictionary<string,string> { ["华硕"]="ASUS", ["微星"]="MSI", ["技嘉"]="Gigabyte", ["华擎"]="ASRock", ["七彩虹"]="Colorful", ["影驰"]="GALAX", ["索泰"]="Zotac", ["铭瑄"]="Maxsun", ["金士顿"]="Kingston", ["威刚"]="ADATA", ["海盗船"]="Corsair", ["利民"]="Thermalright", ["九州风神"]="Deepcool", ["三星"]="Samsung", ["西数"]="Western Digital", ["西部数据"]="Western Digital", ["英特尔"]="Intel", ["英睿达"]="Crucial", ["芝奇"]="G.Skill", ["联力"]="Lian Li", ["乔思伯"]="Jonsbo", ["酷冷至尊"]="Cooler Master", ["航嘉"]="Huntkey", ["振华"]="Super Flower", ["安钛克"]="Antec", ["海韵"]="Seasonic" };
        aliases["锐龙"] = "Ryzen"; aliases["酷睿"] = "Core"; aliases["蓝宝石"] = "Sapphire";
        foreach (var (zh, en) in aliases.OrderByDescending(x => x.Key.Length)) text = text.Replace(zh, " " + en + " ", StringComparison.OrdinalIgnoreCase);
        return text;
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private void Search_Changed(object sender, RoutedEventArgs e) { if (!_ready) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private void Part_Changed(object sender, SelectionChangedEventArgs e) { if (PartsGrid.SelectedItem is PartRow row) ShowPart(row.Part); }
    private void Part_DoubleClick(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridRow>(d) != null) Add_Click(sender, e); }
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject { while (node != null) { if (node is T t) return t; node = VisualTreeHelper.GetParent(node); } return null; }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_detailPart is null) return;
        var p = StorageService.Clone(_detailPart);
        if (_plan.Parts.TryGetValue(p.Category, out var prior) && prior.Id == p.Id) { Log("此型号已在配置单中。"); return; }
        _plan.Parts[p.Category] = p; RefreshAll(); Log("已加入：" + p.Name + "。配置卡右键可报价或移除。");
    }
    private void PlanMeta_Changed(object sender, TextChangedEventArgs e) { if (!_ready || _metaLoading) return; _plan.Name = PlanName.Text; AutoSave(); }
    private void Usage_Changed(object sender, SelectionChangedEventArgs e) { if (!_ready || _metaLoading) return; _plan.Usage = (UsageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "2K 游戏"; RefreshAll(); }
    private void Budget_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _metaLoading) return;
        if (decimal.TryParse(BudgetBox.Text, out var n) && n >= 0 && n <= 10000000) { BudgetBox.BorderBrush = Brush("#D8E0DD"); _plan.Budget = n; RefreshAll(); }
        else BudgetBox.BorderBrush = Brush("#B44C3B");
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PresetBox.SelectedIndex <= 0 || PresetBox.SelectedItem is not BuildPlan p) return;
        _plan = StorageService.Clone(p); NormalizePlan(_plan); SetMeta(); RefreshAll(); Filter(); Log("已加载示例：" + p.Name + "。示例不保证在售，价格需核对。"); PresetBox.SelectedIndex = 0;
    }
    private void New_Click(object sender, RoutedEventArgs e) { _plan = new(); SetMeta(); RefreshAll(); Log("已新建空白配置。"); }
    private void AutoSave()
    {
        if (!_ready || IsSmoke) return;
        try { _plan.SavedAt = DateTimeOffset.Now; StorageService.Write(Path.Combine(StorageService.Root, "autosave.json"), _plan); }
        catch (Exception ex) { Log("自动保存未完成：" + ex.Message); }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "装机配置 (*.pcbuild.json)|*.pcbuild.json", FileName = SafeName(_plan.Name) + ".pcbuild.json" };
        if (dialog.ShowDialog(this) == true) { StorageService.Write(dialog.FileName, _plan); Log("配置已保存：" + dialog.FileName); }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "装机配置 (*.json)|*.json" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(d.FileName).Length > 20_000_000) throw new InvalidDataException("文件过大。");
            var plan = JsonSerializer.Deserialize<BuildPlan>(File.ReadAllText(d.FileName), AppJson.Options) ?? throw new InvalidDataException("内容为空。");
            if (plan.Parts == null || plan.Parts.Count > 50) throw new InvalidDataException("配置格式不正确。");
            NormalizePlan(plan); _plan = plan; SetMeta(); RefreshAll(); Filter(); Log("已打开：" + d.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, "配置无法打开：" + ex.Message, "打开配置"); }
    }
    private static string SafeName(string name) { var s = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))); return string.IsNullOrWhiteSpace(s) ? "我的配置" : s[..Math.Min(s.Length, 70)]; }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "可打印网页清单 (*.html)|*.html|文本清单 (*.txt)|*.txt", FileName = SafeName(_plan.Name) + ".html" };
        if (d.ShowDialog(this) != true) return;
        File.WriteAllText(d.FileName, d.FilterIndex == 2 ? ExportText() : ExportHtml(), new UTF8Encoding(false));
        Log("已导出：" + d.FileName); Launch(d.FileName, true);
    }
    private string ExportText()
    {
        var r = RatingEngine.Evaluate(_plan); var b = new StringBuilder($"{_plan.Name}\n用途：{_plan.Usage} / 预算 ¥{_plan.Budget:N0}\n导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}\n\n");
        foreach (var (k, n) in Categories.All) b.AppendLine(n + "：" + (_plan.Parts.TryGetValue(k, out var p) ? p.Name + " / " + (p.Price.HasValue ? $"¥{p.Price:N2} [{p.PriceKind}]" : "未报价") + "\n规格来源：" + p.SourceUrl + "\n京东搜索：" + SearchUrl(p, true) + "\n淘宝搜索：" + SearchUrl(p, false) : "未选择"));
        b.AppendLine($"\n已录入合计 ¥{_plan.Parts.Values.Sum(p => p.Price ?? 0):N2}（缺价未计）\n评级：{r.Grade} / {r.Score}\n");
        foreach (var f in r.Findings) b.AppendLine($"[{f.Level}] {f.Title}：{f.Detail}");
        b.AppendLine("\n规则诊断不等于实测；电商搜索不保证同SKU或有货，购买前核对型号、接口、套餐、卖家、BIOS与库存。硬件来源 BuildCores OpenDB (CC BY-NC-SA 4.0)，用于个人非商业选型。"); return b.ToString();
    }
    private string ExportHtml()
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var b = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>装机配置单</title><style>body{font:14px 'Microsoft YaHei',sans-serif;max-width:1100px;margin:40px auto;color:#17242d;padding:20px}h1{color:#187a64}table{border-collapse:collapse;width:100%}td,th{padding:13px;border-bottom:1px solid #dde4e0;text-align:left}th{background:#edf4ef}small{color:#68767f}a{color:#187a64}.note{padding:16px;background:#f3f6f3;margin-top:22px}li{margin:10px 0}@media print{body{margin:0;padding:0}a{text-decoration:none}}</style>");
        b.Append($"<h1>{E(_plan.Name)}</h1><p>{E(_plan.Usage)} · 预算 ¥{_plan.Budget:N0} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm}</p><table><tr><th>配件</th><th>型号 / 规格</th><th>录入价格</th><th>购买核对</th></tr>");
        foreach (var (k, name) in Categories.All)
        {
            if (!_plan.Parts.TryGetValue(k, out var p)) { b.Append($"<tr><td>{E(name)}</td><td colspan='3'>未选择</td></tr>"); continue; }
            b.Append($"<tr><td>{E(name)}</td><td>{E(p.Name)}<br><small>{E(PartRow.Describe(p))}<br>{E(p.Source)} · {E(p.UpdatedAt?.ToString("yyyy-MM-dd"))}</small></td><td>{(p.Price.HasValue ? $"¥{p.Price:N2}<br><small>{E(p.PriceKind)}</small>" : "未报价")}</td><td><a href='{E(SearchUrl(p,true))}'>京东搜索</a> / <a href='{E(SearchUrl(p,false))}'>淘宝搜索</a>");
            foreach (var o in p.Offers.Where(o => IsHttp(o.Url)).Take(8)) b.Append($"<br><a href='{E(o.Url)}'>{E(o.Platform)}：{E(o.Shop)} · {(o.Price.HasValue ? $"¥{o.Price:N2}" : "无报价")}</a><small> {E(o.MatchStatus)} · {o.CheckedAt:yyyy-MM-dd}</small>");
            b.Append("</td></tr>");
        }
        b.Append($"</table><h2>已录入价格合计 ¥{_plan.Parts.Values.Sum(p => p.Price ?? 0):N2}</h2><p>缺价未计入；记录价格不等于结算价格。</p>");
        var rating = RatingEngine.Evaluate(_plan); b.Append($"<h2>搭配评级：{E(rating.Grade)} · {rating.Score}/100</h2><ul>");
        foreach (var f in rating.Findings) b.Append($"<li><b>{E(f.Title)}</b>：{E(f.Detail)}</li>");
        b.Append("</ul><div class='note'>规则诊断供选型参考。搜索结果不保证同款或有货，购买前核对具体SKU、套餐、卖家、BIOS、内存QVL及供电接口。<br>硬件规格：<a href='https://github.com/buildcores/buildcores-open-db'>BuildCores OpenDB</a>，CC BY-NC-SA 4.0。此清单供个人非商业使用。</div></html>"); return b.ToString();
    }
    internal static string SearchUrl(Part p, bool jd) => jd ? "https://search.jd.com/Search?keyword=" + Uri.EscapeDataString(p.Name) + "&enc=utf-8" : "https://s.taobao.com/search?q=" + Uri.EscapeDataString(p.Name);
    internal static bool IsHttp(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http");
    internal static void Launch(string target, bool local = false)
    {
        if (!local && !IsHttp(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接：" + ex.Message); }
    }
    private void Jd_Click(object sender, RoutedEventArgs e) { if (_detailPart != null) Launch(SearchUrl(_detailPart, true)); }
    private void Tb_Click(object sender, RoutedEventArgs e) { if (_detailPart != null) Launch(SearchUrl(_detailPart, false)); }
    private void Offers_Click(object sender, RoutedEventArgs e) { if (_detailPart != null) ShowOffers(_detailPart); }
    private void ShowOffers(Part p)
    {
        var actual = _plan.Parts.Values.FirstOrDefault(i => i.Id == p.Id) ?? p;
        new OffersWindow(this, actual, _settings, _online, () => { RefreshAll(); Filter(); if (!_plan.Parts.Values.Any(p => p.Id == actual.Id)) StorageService.Write(Path.Combine(StorageService.Root, "catalog.json"), _catalog); }, Log).ShowDialog();
    }
    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (_detailPart is null) return;
        var p = _plan.Parts.Values.FirstOrDefault(i => i.Id == _detailPart.Id) ?? _detailPart;
        Dialogs.ShowPart(this, p, () => { RefreshAll(); ShowPart(p); if (!_plan.Parts.Values.Any(i => i.Id == p.Id)) StorageService.Write(Path.Combine(StorageService.Root, "catalog.json"), _catalog); });
    }
    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoUpdate = AutoCheck.IsChecked == true; _settings.UpdateHours = int.Parse(((ComboBoxItem)IntervalBox.SelectedItem).Content.ToString()!);
        _settings.JdAppKey = JdKeyBox.Text.Trim(); _settings.JdSecret = JdSecretBox.Password.Trim();
        _settings.TaobaoAppKey = TbKeyBox.Text.Trim(); _settings.TaobaoSecret = TbSecretBox.Password.Trim(); _settings.TaobaoAdzoneId = TbAdzoneBox.Text.Trim();
        StorageService.SaveSettings(_settings); UpdateSyncInfo(); Log("联网设置已加密保存。点击立即同步或更新本配置商品可检查接口。");
    }
    private void UpdateSyncInfo() => SyncInfo.Text = $"硬件同步：{(_settings.LastHardwareSync?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未成功同步")}\n商品查询尝试：{(_settings.LastOfferSync?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未查询")}\n自动任务仅在软件运行时执行；关闭后不驻留后台。";
    private async Task AutoSync()
    {
        if (!_settings.AutoUpdate || _busy) return;
        if (_settings.LastHardwareSync is null || DateTimeOffset.Now - _settings.LastHardwareSync >= TimeSpan.FromHours(_settings.UpdateHours)) await SyncHardware();
        if (!_busy && HasCredentials() && (_settings.LastOfferSync is null || DateTimeOffset.Now - _settings.LastOfferSync >= TimeSpan.FromHours(_settings.UpdateHours))) await SyncBuildOffers();
    }
    private bool HasCredentials() => (!string.IsNullOrWhiteSpace(_settings.JdAppKey) && !string.IsNullOrWhiteSpace(_settings.JdSecret)) || (!string.IsNullOrWhiteSpace(_settings.TaobaoAppKey) && !string.IsNullOrWhiteSpace(_settings.TaobaoSecret) && !string.IsNullOrWhiteSpace(_settings.TaobaoAdzoneId));
    private async void Sync_Click(object sender, RoutedEventArgs e) => await SyncHardware();
    private async Task SyncHardware()
    {
        if (_busy) { Log("已有联网任务进行中。"); return; }
        _busy = true; _cts = new();
        try
        {
            Log("开始同步社区硬件库，首次下载可能需要几分钟…");
            var parts = await _online.UpdateHardwareAsync(new Progress<string>(Log), _cts.Token);
            if (parts.Count == 0) throw new InvalidDataException("数据源没有返回可用硬件，已保留原库。");
            _catalog = _catalog.Concat(parts).GroupBy(p => p.Id).Select(g => g.Last()).ToList();
            await Task.Run(() => StorageService.Write(Path.Combine(StorageService.Root, "catalog.json"), _catalog));
            _settings.LastHardwareSync = DateTimeOffset.Now; StorageService.SaveSettings(_settings);
            RefreshAll(); Filter(); UpdateSyncInfo(); Log($"硬件同步完成：获取 {parts.Count:N0} 项，库内共 {_catalog.Count:N0} 个型号。所选配置保留原规格快照。");
        }
        catch (OperationCanceledException) { Log("联网任务已取消，保留现有硬件库。"); }
        catch (Exception ex) { Log("硬件同步失败，继续使用本地库：" + ex.Message); }
        finally { _busy = false; _cts.Dispose(); _cts = null; }
    }
    private async void SyncOffers_Click(object sender, RoutedEventArgs e) => await SyncBuildOffers();
    private async Task SyncBuildOffers()
    {
        if (_busy) { Log("已有联网任务进行中。"); return; }
        if (!HasCredentials()) { Log("尚未配置完整的官方接口凭据。请先保存联网设置，或直接使用京东、淘宝搜索入口。"); return; }
        _busy = true; _cts = new(); int updated = 0;
        try
        {
            foreach (var p in _plan.Parts.Values.ToList())
            {
                _cts.Token.ThrowIfCancellationRequested(); Log("正在查询商品：" + p.Name);
                var offers = await _online.SearchOffersAsync(p.Name, _settings, new Progress<string>(Log), _cts.Token);
                if (offers.Count > 0) { p.Offers = p.Offers.Where(o => o.MatchStatus.StartsWith("用户已核对")).Concat(offers).GroupBy(o => (o.Url, Verified: o.MatchStatus.StartsWith("用户已核对"))).Select(g => g.First()).ToList(); updated++; }
                await Task.Delay(350, _cts.Token);
            }
            _settings.LastOfferSync = DateTimeOffset.Now; StorageService.SaveSettings(_settings); UpdateSyncInfo(); RefreshAll();
            Log($"商品查询完成：{updated} 项获得候选商品。原报价保持不变；进入商品 / 报价核对后采用。");
        }
        catch (OperationCanceledException) { Log("商品查询已取消。"); }
        catch (Exception ex) { Log("商品查询未完成：" + ex.Message); }
        finally { AutoSave(); _busy = false; _cts.Dispose(); _cts = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); Log(_cts != null ? "正在取消联网任务…" : "当前没有主窗口联网任务。"); }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "硬件库 (*.json)|*.json" }; if (d.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(d.FileName).Length > 80_000_000) throw new InvalidDataException("导入文件不能超过80MB。");
            var parts = JsonSerializer.Deserialize<List<Part>>(File.ReadAllText(d.FileName), AppJson.Options) ?? throw new InvalidDataException("内容为空。");
            if (parts.Count > 100000 || parts.Any(p => p == null || !Categories.All.ContainsKey(p.Category) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Id))) throw new InvalidDataException("请提供有效的 Part 对象数组，包含 Id、Category、Name。");
            foreach (var p in parts) { p.Specs ??= new(); p.Offers ??= new(); p.Source = string.IsNullOrWhiteSpace(p.Source) ? "用户导入" : p.Source; }
            _catalog = _catalog.Concat(parts).GroupBy(p => p.Id).Select(g => g.Last()).ToList(); StorageService.Write(Path.Combine(StorageService.Root, "catalog.json"), _catalog); RefreshAll(); Filter(); Log($"已导入 {parts.Count} 项硬件。");
        }
        catch (Exception ex) { MessageBox.Show(this, "导入未完成：" + ex.Message, "硬件导入"); }
    }
    private void DataFolder_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(StorageService.Root); Launch(StorageService.Root, true); }
    private void JdDocs_Click(object sender, RoutedEventArgs e) => Launch("https://jos.jd.com/apilist?apiGroupId=531");
    private void TbDocs_Click(object sender, RoutedEventArgs e) => Launch("https://developer.alibaba.com/docs/api.htm?apiId=64759");
    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(text)); return; }
        string line = $"{DateTimeOffset.Now:HH:mm:ss}  {text}";
        _logs.Add(line); if (_logs.Count > 250) _logs.RemoveAt(0); StatusText.Text = text; StatusText.ToolTip = text;
    }
    private void Log_Click(object sender, RoutedEventArgs e) => Dialogs.ShowText(this, "联网日志", string.Join(Environment.NewLine + Environment.NewLine, _logs));
    private void Help_Click(object sender, RoutedEventArgs e) => Dialogs.ShowText(this, "使用装机工坊", "1. 点击左侧配件类别，在中间搜索品牌或型号，双击或点击“加入配置”。右键配置卡可移除或录入报价。\n\n2. 右侧实时评估优秀 / 良 / 差。未知关键参数会标为待核验；规则评分不是游戏帧率或真实跑分。\n\n3. 点京东、淘宝按钮在浏览器搜索具体型号。不同套餐、散片/盒装、二手、海外版可能价格不同，务必核对。\n\n4. 在“商品 / 报价”中录入实际价格，或用自己的官方接口凭据查询候选商品，核对型号后采用报价。\n\n5. 在“联网与数据”保存设置，可自动更新硬件库和当前配置的商品候选。首次硬件下载较大；失败时保留缓存。软件关闭后不会继续自动更新。\n\n6. 保存配置为 JSON；导出 HTML 后可打印。配置单自动保存至当前用户 AppData\\Local\\PcWorkbench。\n\n硬件源：BuildCores OpenDB 社区库，不保证每款在国内有货。来源不提供国内价格，内置型号全部未报价。需完整自动商品查询的前提是你自己的京东 / 淘宝应用拥有对应接口权限。\n\n选型时仍需核对 CPU 支持列表与 BIOS、内存QVL、显卡供电线、机箱走线空间以及水冷安装冲突。个人非商业版。");
    private async Task RunSmoke()
    {
        await Task.Delay(900); UpdateLayout();
        var outDir = _args.SkipWhile(a => a != "--smoke").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "smoke");
        Directory.CreateDirectory(outDir);
        // DPAPI round-trip and privacy boundary: a saved plan must never carry API secrets.
        var secretProbe = System.Text.Encoding.UTF8.GetBytes("local-credential-roundtrip");
        var encryptedProbe = Vault.Protect(secretProbe);
        if (encryptedProbe.SequenceEqual(secretProbe) || !Vault.Unprotect(encryptedProbe).SequenceEqual(secretProbe)) throw new Exception("凭据加密自检失败");
        foreach (var preset in _presets)
        {
            var evaluation = RatingEngine.Evaluate(preset);
            if (evaluation.Findings.Any(f => f.Level == "error")) throw new Exception("示例配置存在硬兼容错误：" + preset.Name);
        }
        var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var f = File.Create(Path.Combine(outDir, "workspace.png"))) encoder.Save(f);
        File.WriteAllText(Path.Combine(outDir, "sample.html"), ExportHtml());
        StorageService.Write(Path.Combine(outDir, "roundtrip.pcbuild.json"), _plan);
        var roundtrip = StorageService.Read<BuildPlan>(Path.Combine(outDir, "roundtrip.pcbuild.json"));
        if (roundtrip?.Parts.Count != _plan.Parts.Count) throw new Exception("配置持久化自检失败");
        // Exercise category filtering, selection, adding, manual quote, and rating refresh.
        _category = "gpu"; SearchBox.Text = "RTX"; Filter();
        if (PartsGrid.Items.Count == 0) throw new Exception("显卡搜索自检失败");
        Add_Click(this, new RoutedEventArgs());
        if (_detailPart != null && _plan.Parts.TryGetValue(_detailPart.Category, out var selected)) { selected.Price = 1234; selected.PriceKind = "手动记录（自检）"; }
        RefreshAll(); if (!TotalText.Text.Contains("1,234")) throw new Exception("价格合计自检失败");
        // Exercise the actual quote controls and prevent a prior SKU confirmation carrying to another row.
        var quotePart = _plan.Parts["gpu"];
        quotePart.Offers = new() {
            new Offer { Platform="京东", Title="自检模拟候选A（非真实商品）", Url="https://item.jd.com/1.html", Price=1234 },
            new Offer { Platform="京东", Title="自检模拟候选B（非真实商品）", Url="https://item.jd.com/2.html", Price=2345 }
        };
        var quoteWindow = new OffersWindow(this, quotePart, _settings, _online, RefreshAll, Log);
        quoteWindow.Show(); await Task.Delay(150); quoteWindow.UpdateLayout();
        var quoteGrid = Descendants<DataGrid>(quoteWindow).Single();
        var confirmation = Descendants<CheckBox>(quoteWindow).Single();
        quoteGrid.SelectedIndex = 0; confirmation.IsChecked = true; quoteGrid.SelectedIndex = 1;
        if (confirmation.IsChecked == true) throw new Exception("商品切换未重置核对状态");
        quoteGrid.SelectedIndex = 0; confirmation.IsChecked = true;
        Descendants<Button>(quoteWindow).Single(b => b.Content?.ToString() == "采用所选商品报价").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (quotePart.Price != 1234 || !quotePart.PriceKind.Contains("用户核对")) throw new Exception("采用商品报价自检失败");
        var priceEntry = Descendants<TextBox>(quoteWindow).First(); priceEntry.Text = "1350";
        Descendants<Button>(quoteWindow).Single(b => b.Content?.ToString() == "保存报价").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (quotePart.Price != 1350 || !TotalText.Text.Contains("1,350")) throw new Exception("手动报价控件自检失败");
        quoteWindow.Close();
        Tabs.SelectedIndex = 1; UpdateLayout(); await Task.Delay(200);
        var settingsImage = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); settingsImage.Render(this);
        var enc2 = new PngBitmapEncoder(); enc2.Frames.Add(BitmapFrame.Create(settingsImage)); using (var f = File.Create(Path.Combine(outDir, "settings.png"))) enc2.Save(f);
        File.WriteAllText(Path.Combine(outDir, "ui-result.txt"), $"PASS: {_catalog.Count} parts; initial plan {roundtrip!.Parts.Count}; search {PartsGrid.Items.Count}; add, actual quote controls, SKU confirmation reset, evaluate, save/open, export, settings render, DPAPI roundtrip, 4 presets no hard compatibility errors.");
        Close();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
public class PartRow
{
    public Part Part { get; }
    public PartRow(Part p) => Part = p;
    public string Name => Part.Name;
    public string Summary => Describe(Part);
    public string PriceText => Part.Price.HasValue ? $"¥ {Part.Price:N0}" : "未报价";
    public static string Describe(Part p)
    {
        string[] keys = p.Category switch { "cpu" => new[] { "socket", "cores", "threads", "powerWatts" }, "motherboard" => new[] { "socket", "memoryType", "formFactor", "ramSlots" }, "gpu" => new[] { "chipset", "memoryGb", "powerWatts", "lengthMm" }, "memory" => new[] { "memoryType", "memoryGb", "memorySpeed", "modules" }, "storage" => new[] { "storageGb", "interface", "formFactor" }, "cooler" => new[] { "sockets", "heightMm", "radiatorMm" }, "psu" => new[] { "powerWatts", "formFactor", "efficiency" }, _ => new[] { "formFactor", "supportedForms", "maxGpuMm", "maxCoolerMm" } };
        var values = keys.Where(k => !string.IsNullOrEmpty(p.S(k))).Select(k => k switch { "powerWatts" => p.S(k) + " W", "memoryGb" => p.S(k) + " GB", "storageGb" => p.S(k) + " GB", "memorySpeed" => p.S(k) + " MT/s", "lengthMm" => "长 " + p.S(k) + " mm", "heightMm" => "高 " + p.S(k) + " mm", "ramSlots" => p.S(k) + " 内存槽", "modules" => p.S(k) + " 条", "cores" => p.S(k) + " 核", "threads" => p.S(k) + " 线程", "radiatorMm" => p.S(k) == "0" ? "风冷" : p.S(k) + " mm 冷排", "maxGpuMm" => "显卡 ≤ " + p.S(k) + " mm", "maxCoolerMm" => "散热 ≤ " + p.S(k) + " mm", _ => p.S(k) });
        var result = string.Join(" · ", values); return string.IsNullOrEmpty(result) ? "关键参数待核验" : result;
    }
}
