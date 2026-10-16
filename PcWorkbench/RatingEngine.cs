using System.Globalization;
using System.Text.RegularExpressions;

namespace PcWorkbench;

/// <summary>
/// Conservative, explainable checks against supplied specifications; never a benchmark or QVL guarantee.
/// Platform background: https://www.intel.com/content/www/us/en/support/articles/000092149/processors.html
/// PSU guidance: https://www.corsair.com/ca/en/s/power-supply-calculator
/// The 80 W allowance, 30% PSU reserve and tier comparison below are local planning heuristics.
/// </summary>
public static class RatingEngine
{
    public static RatingResult Evaluate(BuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = new RatingResult();
        var score = 100;
        var incomplete = false;
        var uncertain = false;
        var hardError = false;
        Part? P(string key) => plan.Parts.GetValueOrDefault(key);
        void Add(string level, string title, string detail, int penalty = 0)
        {
            result.Findings.Add(new Finding { Level = level, Title = title, Detail = detail });
            score -= penalty;
            if (level == "error") hardError = true;
        }
        void Unknown(string title, string detail)
        {
            uncertain = true;
            Add("warn", title + "待核验", detail + "；补齐厂商规格后再判断，当前不计为兼容。", 3);
        }
        void Positive(string title, string detail) => Add("good", title, detail);

        var cpu = P("cpu"); var board = P("motherboard"); var gpu = P("gpu");
        var memory = P("memory"); var storage = P("storage"); var cooler = P("cooler");
        var psu = P("psu"); var chassis = P("case");
        foreach (var key in new[] { "cpu", "motherboard", "memory", "storage", "cooler", "psu", "case" })
        {
            if (P(key) is not null) continue;
            incomplete = true;
            Add("warn", "缺少" + Categories.All[key], "请选择" + Categories.All[key] + "。若使用盒装附送散热器，也应添加其型号和规格，才能检查散热与安装空间。", 7);
        }

        void CompareSets(Part a, string aKey, Part b, string bKey, string label, string fix, Func<string, string>? normalize = null)
        {
            normalize ??= Compact;
            var left = Values(a.S(aKey), normalize);
            var right = Values(b.S(bKey), normalize);
            if (left.Count == 0 || right.Count == 0)
                Unknown(label, $"{a.Name} 的 {aKey} 或 {b.Name} 的 {bKey} 未提供");
            else if (!left.Overlaps(right))
                Add("error", label + "不匹配", $"{a.Name}：{a.S(aKey)}；{b.Name}：{b.S(bKey)}。{fix}", 35);
            else
                Positive(label + "匹配", $"已知规格有交集：{string.Join(" / ", left.Intersect(right))}。{(label == "处理器插槽" ? "同插槽仍须在主板官网 CPU 支持列表中确认 BIOS 版本。" : "下单时核对具体后缀与版本。")}");
        }

        if (cpu is not null && board is not null)
            CompareSets(cpu, "socket", board, "socket", "处理器插槽", "更换为相同插槽且在主板 CPU 支持列表中的处理器或主板。");
        if (memory is not null && board is not null)
        {
            CompareSets(memory, "memoryType", board, "memoryType", "内存代际", "DDR4 与 DDR5 不能互插，请更换内存或主板。");
            var modules = Integer(memory, "modules"); var slots = Integer(board, "ramSlots");
            if (modules is null || slots is null)
                Unknown("内存条数与插槽", "缺少有效的套装条数或主板内存插槽数");
            else if (modules > slots)
                Add("error", "内存条数超过插槽", $"选择了 {modules} 条内存，主板仅有 {slots} 个内存插槽。改用更少条数的套装或更多插槽的主板。", 30);
            else
                Positive("内存插槽够用", $"{modules} 条内存 / {slots} 个插槽。安装顺序请按主板手册；频率与 XMP / EXPO 稳定性仍需核对 QVL。");
            var capacity = Number(memory, "memoryGb"); var max = Number(board, "maxMemoryGb");
            if (capacity is null || max is null)
                Unknown("内存容量上限", "缺少内存总容量或主板最大支持容量");
            else if (capacity > max)
                Add("error", "内存容量超过主板上限", $"内存总量 {Fmt(capacity)} GB 超过主板标注的 {Fmt(max)} GB。降低总容量或更换主板。", 30);
            else
                Positive("内存容量在主板上限内", $"{Fmt(capacity)} GB / {Fmt(max)} GB。单条容量与 BIOS 支持仍按主板手册核对。");
            if (modules >= 4)
                Add("warn", "满插内存需要留意频率", "四条及以上内存对内存控制器压力更大，标称超频频率不保证达成。优先两条等容量套装，或按主板 QVL 设置频率。", 3);
        }
        if (cpu is not null && memory is not null && !string.IsNullOrWhiteSpace(cpu.S("memoryType")))
            CompareSets(memory, "memoryType", cpu, "memoryType", "处理器内存支持", "选择处理器与主板共同支持的内存代际。");

        if (board is not null && chassis is not null)
        {
            var form = Form(board.S("formFactor"));
            var supports = Values(chassis.S("supportedForms"), Form);
            var recognized = new HashSet<string> { "ATX", "MATX", "ITX", "EATX", "DTX", "MINIDTX" };
            if (!recognized.Contains(form) || supports.Count == 0 || supports.Any(v => !recognized.Contains(v)))
                Unknown("机箱主板板型", $"主板板型为「{board.S("formFactor")}」，机箱支持「{chassis.S("supportedForms")}」，资料缺失或板型写法无法识别");
            else if (!supports.Contains(form))
                Add("error", "主板放不进所选机箱", $"主板为 {board.S("formFactor")}，机箱仅标注支持 {chassis.S("supportedForms")}。更换明确支持该板型的机箱。", 35);
            else
                Positive("主板板型适配机箱", $"机箱明确支持 {board.S("formFactor")}。E-ATX 等非统一宽度板型还需核对实际长宽与走线空间。");
        }
        if (gpu is not null && chassis is not null)
        {
            var length = Number(gpu, "lengthMm"); var limit = Number(chassis, "maxGpuMm");
            if (length is null || limit is null)
                Unknown("显卡长度", "缺少具体非公版显卡长度或机箱显卡限长");
            else if (length > limit)
                Add("error", "显卡超过机箱限长", $"显卡长 {Fmt(length)} mm，机箱限长 {Fmt(limit)} mm。更换更短显卡或更大机箱；前置冷排还可能占用空间。", 35);
            else if (limit - length < 20)
                Add("warn", "显卡安装余量较小", $"长度仅余 {Fmt(limit - length)} mm。核对前置风扇、冷排和供电插头弯折空间。", 6);
            else
                Positive("显卡长度有余量", $"显卡 {Fmt(length)} mm，机箱限长 {Fmt(limit)} mm，剩余 {Fmt(limit - length)} mm。厚度及供电插头空间需另行核对。");
        }
        if (psu is not null && chassis is not null)
        {
            var form = PsuForm(psu.S("formFactor"));
            var supports = Values(chassis.S("supportedPsuForms"), PsuForm);
            var recognized = new HashSet<string> { "ATX", "SFX", "SFXL", "TFX", "FLEXATX", "CFX", "LFX" };
            if (!recognized.Contains(form) || supports.Count == 0 || supports.Any(v => !recognized.Contains(v)))
                Unknown("机箱电源尺寸标准", $"电源为「{psu.S("formFactor")}」，机箱支持「{chassis.S("supportedPsuForms")}」，资料缺失或写法无法识别");
            else if (!supports.Contains(form))
                Add("error", "机箱未标注支持所选电源规格", $"电源为 {psu.S("formFactor")}，机箱仅标注支持 {chassis.S("supportedPsuForms")}。更换相符的电源或机箱；若用转接支架，须先核对机箱官方安装说明，不能直接认定兼容。", 35);
            else
                Positive("电源尺寸标准适配机箱", $"机箱明确标注支持 {psu.S("formFactor")} 电源。若说明要求专用支架，请确认支架是否附送。");
            var length = Number(psu, "lengthMm"); var limit = Number(chassis, "maxPsuMm");
            if (length is null || limit is null)
                Unknown("电源长度", "缺少电源本体长度或机箱电源限长");
            else if (length > limit)
                Add("error", "电源超过机箱限长", $"电源长 {Fmt(length)} mm，机箱限长 {Fmt(limit)} mm。更换更短电源或机箱；同时留意硬盘笼位置与模组线插头。", 35);
            else if (limit - length < 20)
                Add("warn", "电源走线余量较小", $"电源本体与标注限长仅余 {Fmt(limit - length)} mm。核对模组线插头、线材弯折空间及硬盘笼是否需要拆除。", 6);
            else
                Positive("电源长度有安装余量", $"电源 {Fmt(length)} mm，机箱限长 {Fmt(limit)} mm。实际线材和硬盘笼空间仍按机箱手册核对。");
        }
        if (cpu is not null && cooler is not null)
        {
            CompareSets(cpu, "socket", cooler, "sockets", "散热器扣具", "选择支持该插槽且随附相应扣具的散热器。");
            var cpuWatts = Number(cpu, "powerWatts"); var cooling = Number(cooler, "coolerWatts");
            if (cpuWatts is null || cooling is null)
                Unknown("散热能力", "缺少处理器功耗或散热器标称散热能力");
            else if (cooling < cpuWatts)
                Add("warn", "散热能力偏弱", $"CPU 标注功耗 {Fmt(cpuWatts)} W，散热器标称 {Fmt(cooling)} W。持续负载可能降频；建议更强散热器或限制 CPU 功耗。不同厂商散热 TDP 口径不统一，此项为经验判断。", cooling < cpuWatts * .7 ? 25 : 15);
            else if (cooling < cpuWatts * 1.2)
                Add("warn", "散热裕量有限", $"标称散热能力 {Fmt(cooling)} W 对 CPU {Fmt(cpuWatts)} W 余量较少。重负载建议预留更多散热能力，结合实际评测核对噪声与温度。", 6);
            else
                Positive("散热标称裕量充足", $"散热器标称 {Fmt(cooling)} W，CPU 标注功耗 {Fmt(cpuWatts)} W。此项仅为标称值比较，不代表实测温度。");
        }
        if (cooler is not null && chassis is not null)
        {
            var radiator = Number(cooler, "radiatorMm", allowZero: true);
            if (radiator is null)
                Unknown("散热器类型与空间", "未明确散热器是风冷（radiatorMm=0）还是水冷（正数冷排尺寸）");
            else if (radiator == 0)
            {
                var height = Number(cooler, "heightMm"); var limit = Number(chassis, "maxCoolerMm");
                if (height is null || limit is null)
                    Unknown("风冷高度", "缺少散热器高度或机箱风冷限高");
                else if (height > limit)
                    Add("error", "散热器超过机箱限高", $"散热器高 {Fmt(height)} mm，机箱限高 {Fmt(limit)} mm。更换更低散热器或更大机箱。", 35);
                else if (limit - height < 3)
                    Add("warn", "风冷高度接近上限", $"散热器与侧板仅余 {Fmt(limit - height)} mm。核对尺寸公差及内存顶起风扇后的总高度。", 5);
                else
                    Positive("风冷高度适配", $"散热器 {Fmt(height)} mm，机箱限高 {Fmt(limit)} mm。仍需核对内存马甲与散热器避让。");
            }
            else
            {
                var sizes = Values(chassis.S("supportedRadiators"), Compact);
                if (sizes.Count == 0)
                    Unknown("水冷冷排安装位", $"冷排为 {Fmt(radiator)} mm，机箱冷排安装位未提供");
                else if (!sizes.Contains(Compact(Fmt(radiator))) && !sizes.Contains(Compact(Fmt(radiator) + "mm")))
                    Add("error", "机箱不支持所选冷排", $"所选冷排 {Fmt(radiator)} mm，机箱仅标注 {chassis.S("supportedRadiators")}。更换冷排或机箱，并核对冷排加风扇的厚度。", 35);
                else
                    Positive("冷排规格有对应安装位", $"机箱标注支持 {Fmt(radiator)} mm 冷排。具体顶部/前部安装位、厚度及与显卡内存的冲突仍需按手册确认。");
            }
        }

        if (cpu is not null && gpu is null)
        {
            var igpu = cpu.B("igpu");
            if (igpu is false)
                Add("error", "没有可用的图形输出", "该 CPU 标注无核显，配置中也没有独立显卡。添加独显，或换有核显的 CPU，并核对主板显示输出接口。", 40);
            else if (igpu is null)
                Unknown("无独显时的显示输出", "CPU 是否具有核显尚不明确，请核对精确型号");
            else
            {
                Positive("可使用核显输出", "CPU 标注有核显。还需主板具备显示输出接口，并使用正确的输出连接。");
                if (ContainsAny(plan.Usage, "游戏", "3D", "AI", "渲染"))
                    Add("warn", "核显可能限制当前用途", $"当前用途为「{plan.Usage}」，仅核显不能据此认定能满足游戏、渲染或 AI 需求。请按目标软件/游戏与分辨率核对实测，必要时添加独显。", 18);
            }
        }

        if (storage is not null && board is not null)
        {
            var bus = Compact(storage.S("interface"));
            var isM2 = bus.Contains("NVME") || bus.Contains("M.2") || bus.Contains("M2");
            var isSata = bus.Contains("SATA");
            // M.2 SATA requires a SATA-capable M.2 slot; a generic M.2 slot count cannot prove that.
            if (isM2 && isSata)
                Unknown("M.2 SATA 协议", "所选硬盘为 M.2 SATA，单凭 M.2 插槽数无法判断协议支持，请核对主板对应插槽手册");
            else if (!isM2 && !isSata)
                Unknown("存储接口", "硬盘接口不明确或当前规则无法识别，需核对 NVMe / M.2 / SATA 协议");
            else
            {
                var key = isM2 ? "m2Slots" : "sataPorts";
                var count = Integer(board, key, allowZero: true);
                if (count is null)
                    Unknown("主板存储连接", "主板对应接口数量未提供");
                else if (count == 0)
                    Add("error", "主板缺少对应硬盘接口", $"硬盘为 {storage.S("interface")}，主板标注 {key}=0。更换硬盘或选择有对应接口的主板。", 30);
                else
                    Positive("主板有对应存储接口", $"硬盘为 {storage.S("interface")}，对应接口 {count} 个。M.2 长度、PCIe 通道共享和 SATA 端口禁用规则请按主板手册核对。");
            }
            var capacity = Number(storage, "storageGb");
            if (capacity is null)
                Unknown("硬盘容量", "所选硬盘容量未提供");
            else if (capacity < 500 && ContainsAny(plan.Usage, "游戏", "剪辑", "视频", "渲染"))
                Add("warn", "存储容量偏紧", $"{Fmt(capacity)} GB 可能很快被系统、游戏或素材占满。建议至少 1 TB，并按实际素材量预留空间。", 5);
        }

        if (cpu is not null)
        {
            var cpuPower = Number(cpu, "powerWatts");
            var gpuPower = gpu is null ? 0d : Number(gpu, "powerWatts");
            var supply = psu is null ? null : Number(psu, "powerWatts");
            if (Compact(cpu.S("powerBasis")) is "TDP" or "PBP" or "BASEPOWER")
                Unknown("CPU 最大功耗", $"当前 {Fmt(cpuPower)} W 来自 {cpu.S("powerBasis")}，基础/热设计功耗不等于最大睿频功耗。下列电源估算仅为参考，请补充官方 MTP / PPT 或实际功耗限制，并重新评估电源及散热裕量");
            // No estimate is exposed as a complete estimate when CPU/GPU power is missing.
            if (cpuPower is null || gpuPower is null)
            {
                Unknown("整机功耗", "CPU 或独显功耗缺失，不能生成可靠的整机功耗与电源建议；请填写厂商最大/板卡功耗而非仅基础功耗");
                var known = (cpuPower ?? 0) + (gpuPower ?? 0);
                if (supply is not null && known > supply)
                    Add("error", "电源低于已知部件功耗", $"仅已知 CPU / 显卡功耗已达 {Fmt(known)} W，超过电源 {Fmt(supply)} W。更换电源并补齐其余功耗。", 40);
            }
            else
            {
                var estimate = cpuPower.Value + gpuPower.Value + 80;
                result.EstimatedWatts = (int)Math.Ceiling(Math.Min(estimate, int.MaxValue));
                result.RecommendedPsu = (int)Math.Min(int.MaxValue, Math.Max(350, Math.Ceiling(estimate * 1.3 / 50) * 50));
                Add("info", "功耗为经验估算", $"CPU {Fmt(cpuPower)} W + 显卡 {Fmt(gpuPower)} W + 其余部件预留 80 W ≈ {result.EstimatedWatts} W；按约 30% 裕量取整，建议电源 ≥ {result.RecommendedPsu} W。这不是墙上实测功耗；显卡厂商的电源要求、瞬时峰值和供电接口优先。");
                if (psu is not null)
                {
                    if (supply is null)
                        Unknown("电源额定功率", "电源额定功率缺失或无效");
                    else if (supply < estimate)
                        Add("error", "电源功率不足", $"电源 {Fmt(supply)} W 低于估算负载 {result.EstimatedWatts} W。建议至少 {result.RecommendedPsu} W，并核对显卡厂商推荐功率及 CPU / 显卡供电接口。", 40);
                    else if (supply < result.RecommendedPsu)
                        Add("warn", "电源裕量偏小", $"电源 {Fmt(supply)} W，高于估算负载但低于经验建议 {result.RecommendedPsu} W。建议提高功率并核对供电线与瞬时负载。", 12);
                    else
                        Positive("电源功率有规划裕量", $"额定 {Fmt(supply)} W，经验建议至少 {result.RecommendedPsu} W。额定瓦数不能代表电源品质；请核对可靠性、接口与原装线材。");
                }
            }
        }

        if (memory is not null)
        {
            var ram = Number(memory, "memoryGb");
            var heavy = ContainsAny(plan.Usage, "剪辑", "视频", "渲染", "AI", "生产力", "开发");
            var gaming = ContainsAny(plan.Usage, "游戏", "3D");
            var minimum = heavy ? 32 : gaming ? 16 : 8;
            var comfortable = heavy ? 64 : gaming ? 32 : 16;
            if (ram is not null && ram < minimum)
                Add("warn", "内存不利于当前用途", $"「{plan.Usage}」仅配 {Fmt(ram)} GB。经验下限约 {minimum} GB，建议按项目/游戏需求增加；此阈值不替代软件官方要求。", 20);
            else if (ram is not null && ram < comfortable)
                Add("info", "内存满足常见基础需求", $"{Fmt(ram)} GB 达到当前用途的经验下限。多任务或大型项目可考虑 {comfortable} GB，按实际工作负载决定。");
            else if (ram is not null)
                Positive("内存为当前用途留有空间", $"{Fmt(ram)} GB 达到「{plan.Usage}」的经验规划档位，仍应按软件与项目规模确认需求。");
        }
        if (cpu is not null && gpu is not null && ContainsAny(plan.Usage, "游戏", "3D", "综合"))
        {
            var cpuTier = Number(cpu, "cpuTier"); var gpuTier = Number(gpu, "gpuTier");
            if (cpuTier is null || gpuTier is null || cpuTier > 5 || gpuTier > 5)
                Add("info", "性能平衡尚未判断", "CPU / 显卡缺少有效的 1–5 档经验等级。没有依据型号名编造跑分或瓶颈百分比，请按目标游戏、分辨率及实测核对。");
            else if (Math.Abs(cpuTier.Value - gpuTier.Value) >= 3)
                Add("warn", "CPU 与显卡预算档位相差较大", $"CPU 经验档 {Fmt(cpuTier)} / 5，显卡 {Fmt(gpuTier)} / 5。可考虑把部分预算转向较弱一端；具体瓶颈取决于软件、分辨率与帧率目标，这不是跑分结论。", 12);
            else
                Positive("CPU 与显卡档位较均衡", "两者经验档位差小于 3 档，仅表明大致定位接近，不保证具体游戏帧率或不存在瓶颈。");
        }

        var total = plan.Parts.Values.Sum(p => Math.Max(0, p.Price ?? 0));
        var missingPrices = plan.Parts.Values.Where(p => p.Price is null || p.Price <= 0).ToList();
        if (missingPrices.Count > 0)
            Add("warn", "总价尚不完整", $"{missingPrices.Count} 个配件未填写有效价格。目前已知小计 ¥{total:0.##}，不能据此判断最终落地总价。补充同型号商品实付价。", 4);
        if (plan.Budget > 0 && total > plan.Budget)
            Add("warn", "超出预算", $"已知配件小计 ¥{total:0.##}，超过预算 ¥{plan.Budget:0.##}，超支 ¥{total - plan.Budget:0.##}。优先调整不影响用途的外观、主板或容量档位。", total > plan.Budget * 1.2m ? 15 : 8);
        else if (plan.Budget > 0 && missingPrices.Count == 0 && plan.Parts.Count > 0)
            Positive("已知报价在预算内", $"小计 ¥{total:0.##} / 预算 ¥{plan.Budget:0.##}；运费、优惠、装机服务和操作系统费用请另行核对。");

        var unverified = plan.Parts.Values.Count(p => !VerifiedPrice(p));
        if (unverified > 0)
            Add("warn", "商品报价未全部核对", $"{unverified} 个配件没有近 7 天、用户核对型号且价格一致的京东 / 淘宝具体商品记录。目录参考价、关键词候选与搜索入口不能作为已核对报价。请打开具体商品页核对型号、套餐与记录价格。", 3);
        else if (plan.Parts.Count > 0)
            Positive("已关联近期核对的商品报价", "所选配件均有近期核对型号且价格一致的京东 / 淘宝具体商品记录。此项只检查本地报价记录，不证明现在有货或现在仍是该价格。");
        Add("info", "库存与到手价需在下单时确认", "本软件没有实时库存保证。即使你已核对型号和采用报价，卖家、地区、库存、运费、优惠与最终结算价仍须在京东 / 淘宝下单页确认。");
        Add("info", "评级范围", "评级依据所填规格与本地规则，优秀也不等于整机验证通过。BIOS / QVL、机箱实际安装位、供电接口、固件与商品真伪仍需按厂商资料及下单页面核对。分数是规则评分，不是性能跑分。");
        result.Score = Math.Clamp(score, 0, 100);
        if (hardError) { result.Grade = "差"; result.Score = Math.Min(result.Score, 49); }
        else if (incomplete) { result.Grade = "待完善"; result.Score = Math.Min(result.Score, 59); }
        else if (result.Score < 60) result.Grade = "差";
        else if (!uncertain && result.Score >= 85) result.Grade = "优秀";
        else { result.Grade = "良"; if (uncertain) result.Score = Math.Min(result.Score, 79); }
        // Show critical actions first without disturbing order within a severity.
        result.Findings = result.Findings.OrderBy(f => f.Level == "error" ? 0 : f.Level == "warn" ? 1 : f.Level == "good" ? 2 : 3).ToList();
        return result;
    }

    private static bool VerifiedPrice(Part part)
    {
        if (part.Price is null || part.Price <= 0) return false;
        return part.Offers.Any(o => o is not null &&
            (o.Platform.Contains("京东") || o.Platform.Contains("淘宝") || o.Platform.Contains("天猫")) &&
            o.Price == part.Price && o.CheckedAt <= DateTimeOffset.Now.AddMinutes(5) &&
            o.CheckedAt >= DateTimeOffset.Now.AddDays(-7) &&
            o.MatchStatus is "用户已核对型号 / 库存待核验" or "用户已核对 / 手动记录" or "已核验" or "精确匹配" or "已精确匹配" &&
            Uri.TryCreate(o.Url, UriKind.Absolute, out var url) &&
            url.Scheme == "https" && IsProductPage(url));
    }
    private static bool IsProductPage(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        if (host is "item.jd.com" or "item.m.jd.com")
            return Regex.IsMatch(url.AbsolutePath, @"^/(product/)?\d+\.html$", RegexOptions.IgnoreCase);
        if (host is "item.taobao.com" or "detail.tmall.com" or "detail.m.tmall.com" or "h5.m.taobao.com")
            return url.AbsolutePath.EndsWith("/item.htm", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(url.Query, @"[?&]id=\d+(?:&|$)");
        if (host == "a.m.taobao.com")
            return Regex.IsMatch(url.AbsolutePath, @"^/i\d+\.htm$", RegexOptions.IgnoreCase);
        return false;
    }
    private static bool ContainsAny(string? text, params string[] terms) => terms.Any(t => (text ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
    private static double? Number(Part p, string key, bool allowZero = false)
    {
        var n = p.N(key);
        return n is not null && double.IsFinite(n.Value) && n >= (allowZero ? 0 : double.Epsilon) ? n : null;
    }
    private static double? Integer(Part p, string key, bool allowZero = false)
    {
        var n = Number(p, key, allowZero);
        return n is not null && Math.Truncate(n.Value) == n ? n : null;
    }
    private static string Fmt(double? n) => n?.ToString("0.##", CultureInfo.InvariantCulture) ?? "未知";
    private static HashSet<string> Values(string? raw, Func<string, string> normalize)
    {
        if (UnknownText(raw)) return new(StringComparer.OrdinalIgnoreCase);
        return (raw ?? "").Split(new[] { ',', '，', ';', '；', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !UnknownText(s)).Select(normalize).Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private static bool UnknownText(string? raw) => Compact(raw ?? "") is "" or "UNKNOWN" or "N/A" or "NA" or "未知" or "待核验" or "待确认" or "待补充" or "不详" or "?" or "NULL";
    private static string Compact(string raw) => Regex.Replace(raw.Trim().ToUpperInvariant(), @"[\s\-_]", "");
    private static string Form(string raw) => Compact(raw).Replace("Μ", "U").Replace("µ", "U") switch
    {
        "MICROATX" or "UATX" or "MATX" => "MATX",
        "MINIITX" or "MITX" or "ITX" => "ITX",
        "EXTENDEDATX" or "EATX" => "EATX",
        var other => other
    };
    private static string PsuForm(string raw) => Compact(raw ?? "") switch
    {
        "ATX12V" or "ATXPS2" or "PS2" => "ATX",
        "SFX12V" => "SFX",
        "FLEXATX12V" => "FLEXATX",
        var other => other
    };
}
