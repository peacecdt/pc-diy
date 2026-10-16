using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace PcWorkbench;
public static class Dialogs
{
    public static void ShowText(Window owner, string title, string text)
    {
        var w = new Window { Owner = owner, Title = title, Width = 800, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        w.Content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20), Background = Brushes.White };
        w.ShowDialog();
    }
    public static void ShowPart(Window owner, Part p, Action? changed = null)
    {
        var w = new Window { Owner = owner, Title = "详细规格 · " + p.Name, Width = 720, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = p.Name, FontSize = 20, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = $"来源：{p.Source}\n获取时间：{p.UpdatedAt:yyyy-MM-dd HH:mm zzz}\n{p.SourceUrl}", Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 10) });
        var source = new Button { Content = "查看原始来源", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14), IsEnabled = MainWindow.IsHttp(p.SourceUrl) }; source.Click += (_, _) => MainWindow.Launch(p.SourceUrl); panel.Children.Add(source);
        panel.Children.Add(new TextBlock { Text = "可据厂商规格补充或修订参数。只填写已核实的内容；未知留空。多值用英文逗号，数值不带单位。", Margin = new Thickness(0, 0, 0, 16) });
        var labels = new Dictionary<string, string>
        {
            ["socket"]="CPU 插槽（AM5 / LGA1700 等）",["memoryType"]="内存代际（DDR4 / DDR5）",["formFactor"]="板型 / 电源尺寸标准",["supportedForms"]="机箱支持板型（ATX,Micro ATX,Mini ITX）",["sockets"]="散热支持的 CPU 插槽",["powerWatts"]="峰值功耗 / 电源额定瓦数 W",["lengthMm"]="显卡 / 电源长度 mm",["maxGpuMm"]="机箱显卡限长 mm",["heightMm"]="散热器高度 mm",["maxCoolerMm"]="机箱散热限高 mm",["radiatorMm"]="冷排尺寸 mm（风冷填 0）",["supportedRadiators"]="机箱支持冷排尺寸（240,280,360）",["memoryGb"]="内存套装总容量 / 显存 GB",["memorySpeed"]="内存传输率 MT/s",["modules"]="套装内存条数",["ramSlots"]="主板内存插槽数",["maxMemoryGb"]="主板最大内存容量 GB",["storageGb"]="硬盘容量 GB",["interface"]="存储接口",["m2Slots"]="主板 M.2 插槽数",["sataPorts"]="主板 SATA 接口数",["igpu"]="CPU 有核显（true / false）",["coolerWatts"]="散热器可承受功耗 W（厂商数据）",["cpuTier"]="CPU 经验档位 1—5（非跑分）",["gpuTier"]="GPU 经验档位 1—5（非跑分）"
        };
        labels["supportedPsuForms"] = "机箱支持电源标准（ATX,SFX,SFX-L）"; labels["maxPsuMm"] = "机箱电源限长 mm";
        string[] relevant = p.Category switch { "cpu" => new[] {"socket","memoryType","powerWatts","igpu","cpuTier"}, "motherboard" => new[] {"socket","memoryType","formFactor","ramSlots","maxMemoryGb","m2Slots","sataPorts"}, "gpu" => new[] {"powerWatts","lengthMm","gpuTier"}, "memory" => new[] {"memoryType","memoryGb","memorySpeed","modules"}, "storage" => new[] {"storageGb","interface","formFactor"}, "cooler" => new[] {"sockets","heightMm","radiatorMm","coolerWatts"}, "psu" => new[] {"powerWatts","formFactor","lengthMm"}, _ => new[] {"supportedForms","maxGpuMm","maxCoolerMm","supportedRadiators","supportedPsuForms","maxPsuMm"} };
        var boxes = new Dictionary<string, TextBox>();
        foreach (var key in relevant.Concat(p.Specs.Keys).Distinct())
        {
            panel.Children.Add(new TextBlock { Text = labels.GetValueOrDefault(key, key), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var box = new TextBox { Text = p.S(key), Margin = new Thickness(0, 0, 0, 12), ToolTip = key }; boxes[key] = box; panel.Children.Add(box);
        }
        var save = new Button { Content = "保存已核验的参数", Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(0, 10, 0, 0) };
        save.Click += (_, _) =>
        {
            p.Specs = boxes.Where(x => !string.IsNullOrWhiteSpace(x.Value.Text)).ToDictionary(x => x.Key, x => x.Value.Text.Trim());
            p.Specs["manualVerifiedAt"] = DateTimeOffset.Now.ToString("O");
            if (!p.Source.Contains("用户修订")) p.Source += " / 用户修订";
            changed?.Invoke(); w.Close();
        };
        panel.Children.Add(save); w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; w.ShowDialog();
    }
}
public class OffersWindow : Window
{
    private readonly Part _part;
    private readonly AppSettings _settings;
    private readonly OnlineService _online;
    private readonly Action _changed;
    private readonly Action<string> _log;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 10, 0, 10), Foreground = Brushes.DimGray };
    private readonly DataGrid _grid = new() { MinHeight = 160 };
    private readonly CancellationTokenSource _cts = new();
    public OffersWindow(Window owner, Part part, AppSettings settings, OnlineService online, Action changed, Action<string> log)
    {
        Owner = owner; Title = "商品 / 报价 · " + part.Name; Width = 1000; Height = 730; MinWidth = 850; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _part = part; _settings = settings; _online = online; _changed = changed; _log = log;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new StackPanel(); head.Children.Add(new TextBlock { Text = part.Name, FontSize = 20, FontWeight = FontWeights.Bold });
        head.Children.Add(new TextBlock { Text = "官方接口返回的是关键词候选。必须核对具体型号、规格套餐与卖家；价格和库存以结算页为准。", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12) });
        var buttons = new WrapPanel();
        var query = new Button { Content = "联网查询候选商品", Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0) };
        query.Click += async (_, _) =>
        {
            query.IsEnabled = false; _status.Text = "正在查询…";
            try
            {
                var progress = new Progress<string>(s => { _status.Text = s; _log(s); });
                var results = await _online.SearchOffersAsync(part.Name, settings, progress, _cts.Token);
                if (_cts.IsCancellationRequested) return;
                if (results.Count > 0)
                {
                    part.Offers = part.Offers.Where(o => o.MatchStatus.StartsWith("用户已核对")).Concat(results).GroupBy(o => (o.Url, Verified: o.MatchStatus.StartsWith("用户已核对"))).Select(g => g.First()).ToList(); Refresh(); _changed();
                    _status.Text = $"获取 {results.Count} 条候选。选择后可打开商品页；核对后再采用报价。";
                }
                else _status.Text += "\n未取得候选商品。可在浏览器搜索，或直接录入你核实的报价。";
            }
            catch (OperationCanceledException) { _status.Text = "查询已取消。"; }
            catch (Exception ex) { _status.Text = "查询未完成：" + ex.Message; _log(_status.Text); }
            finally { query.IsEnabled = true; }
        };
        buttons.Children.Add(query);
        var jd = new Button { Content = "京东搜同款", Margin = new Thickness(0, 0, 10, 0) }; jd.Click += (_, _) => MainWindow.Launch(MainWindow.SearchUrl(part, true)); buttons.Children.Add(jd);
        var tb = new Button { Content = "淘宝搜同款", Margin = new Thickness(0, 0, 10, 0) }; tb.Click += (_, _) => MainWindow.Launch(MainWindow.SearchUrl(part, false)); buttons.Children.Add(tb);
        var open = new Button { Content = "打开所选商品" }; open.Click += (_, _) => { if (_grid.SelectedItem is Offer o) MainWindow.Launch(o.Url); }; buttons.Children.Add(open);
        head.Children.Add(buttons); head.Children.Add(_status); root.Children.Add(head);
        foreach (var (title, field, width) in new[] { ("平台", "Platform", 70), ("商品标题", "Title", 320), ("店铺", "Shop", 140), ("参考价格", "Price", 90), ("核对状态", "MatchStatus", 190), ("查询时间", "CheckedAt", 150) }) _grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(field) { StringFormat = field == "CheckedAt" ? "yyyy-MM-dd HH:mm" : field == "Price" ? "¥ {0:N2}" : null }, Width = width });
        Grid.SetRow(_grid, 1); root.Children.Add(_grid);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(footer, 2);
        var verified = new CheckBox { Content = "我已核对所选商品的型号与套餐，确认与此配件一致", Margin = new Thickness(0, 0, 0, 10) }; footer.Children.Add(verified);
        _grid.SelectionChanged += (_, _) => verified.IsChecked = false;
        var adopt = new Button { Content = "采用所选商品报价", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18) };
        adopt.Click += (_, _) =>
        {
            if (_grid.SelectedItem is not Offer o) { _status.Text = "请先选择一条候选商品。"; return; }
            if (verified.IsChecked != true) { _status.Text = "请先打开商品页，核对具体型号和套餐后勾选确认。"; return; }
            if (o.Price is null or < 0) { _status.Text = "所选商品无有效价格，请手动录入。"; return; }
            o.MatchStatus = "用户已核对型号 / 库存待核验";
            part.Offers.RemoveAll(x => !ReferenceEquals(x, o) && x.Url == o.Url && x.MatchStatus.StartsWith("用户已核对"));
            part.Price = o.Price; part.PriceKind = $"{o.Platform} 用户核对报价 {o.CheckedAt:yyyy-MM-dd HH:mm}"; _changed(); Refresh(); _status.Text = "报价已采用，合计与评级已更新。";
        };
        footer.Children.Add(adopt);
        footer.Children.Add(new TextBlock { Text = "手动记录报价（人民币）", FontWeight = FontWeights.Bold });
        var manual = new Grid { Margin = new Thickness(0, 8, 0, 0) }; manual.ColumnDefinitions.Add(new() { Width = new GridLength(150) }); manual.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); manual.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var priceBox = new TextBox { Text = part.Price?.ToString(CultureInfo.InvariantCulture) ?? "", Margin = new Thickness(0, 0, 10, 0), ToolTip = "人民币金额；清空表示未报价" }; manual.Children.Add(priceBox);
        var urlBox = new TextBox { ToolTip = "可选：粘贴已核验的商品链接（https://…）", Margin = new Thickness(0, 0, 10, 0) }; Grid.SetColumn(urlBox, 1); manual.Children.Add(urlBox);
        var record = new Button { Content = "保存报价" }; Grid.SetColumn(record, 2); manual.Children.Add(record);
        record.Click += (_, _) =>
        {
            decimal? value = null;
            if (!string.IsNullOrWhiteSpace(priceBox.Text))
            {
                if (!decimal.TryParse(priceBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 10000000) { _status.Text = "请输入 0 到 10000000 的人民币金额。"; return; } value = n;
            }
            string url = urlBox.Text.Trim();
            if (url.Length > 0 && !MainWindow.IsHttp(url)) { _status.Text = "商品链接须以 https:// 或 http:// 开头。"; return; }
            part.Price = value; part.PriceKind = value.HasValue ? "手动记录 " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") : "未报价";
            if (url.Length > 0) part.Offers.Insert(0, new Offer { Platform = new Uri(url).Host.Contains("jd.com") ? "京东" : new Uri(url).Host.Contains("taobao.com") || new Uri(url).Host.Contains("tmall.com") ? "淘宝/天猫" : "手动链接", Title = part.Name, Url = url, Price = value, MatchStatus = "用户已核对 / 手动记录" });
            _changed(); Refresh(); _status.Text = "手动报价已保存。价格不会被自动查询覆盖。";
        };
        footer.Children.Add(manual); footer.Children.Add(new TextBlock { Text = "左框金额，右框可选填已核验商品链接。未加入配置的型号，报价保留在本次硬件库记录中。", FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0) }); root.Children.Add(footer);
        Content = root; Refresh(); _status.Text = part.Offers.Count == 0 ? "暂无候选商品。未配置密钥时可直接使用上方搜索入口。" : $"已缓存 {part.Offers.Count} 个候选商品，请查看查询时间。";
        Closing += (_, _) => _cts.Cancel();
    }
    private void Refresh() { _grid.ItemsSource = null; _grid.ItemsSource = _part.Offers; }
}
