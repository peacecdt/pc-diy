using PcWorkbench;

var tests = new (string Name, Action<BuildPlan> Change, Action<RatingResult> Check)[]
{
    ("完整相容方案可以优秀", _ => {}, r => Is(r.Grade == "优秀", Describe(r))),
    ("空配置待完善", p => p.Parts.Clear(), r => Is(r.Grade == "待完善", Describe(r))),
    ("CPU插槽错误", p => Set(p,"cpu","socket","LGA1700"), r => Error(r,"处理器插槽不匹配")),
    ("DDR代际错误", p => Set(p,"memory","memoryType","DDR4"), r => Error(r,"内存代际不匹配")),
    ("未知内存不能算兼容", p => Set(p,"memory","memoryType",""), r => Unknown(r,"内存代际")),
    ("N/A内存按未知处理", p => Set(p,"memory","memoryType","N/A"), r => {Unknown(r,"内存代际");Is(!r.Findings.Any(f=>f.Level=="error"),Describe(r));}),
    ("主板放不进机箱", p => Set(p,"case","supportedForms","Mini-ITX"), r => Error(r,"主板放不进")),
    ("microATX别名规范化", p => {Set(p,"motherboard","formFactor","micro-ATX");Set(p,"case","supportedForms","ATX,mATX,Mini ITX");}, r => Is(!r.Findings.Any(f=>f.Title.Contains("机箱主板")||f.Level=="error"),Describe(r))),
    ("希腊muATX规范化", p => Set(p,"motherboard","formFactor","μATX"), r => Is(!r.Findings.Any(f=>f.Title.Contains("机箱主板")||f.Level=="error"),Describe(r))),
    ("未知板型不能放行", p => Set(p,"motherboard","formFactor","大型板"), r => Unknown(r,"机箱主板板型")),
    ("显卡超长", p => Set(p,"gpu","lengthMm","500"), r => Error(r,"显卡超过机箱")),
    ("显卡未知长度", p => Set(p,"gpu","lengthMm","未知"), r => Unknown(r,"显卡长度")),
    ("电源尺寸标准不匹配", p => Set(p,"case","supportedPsuForms","SFX,SFX-L"), r => Error(r,"机箱未标注支持所选电源规格")),
    ("电源标准缺失", p => Set(p,"psu","formFactor",""), r => Unknown(r,"机箱电源尺寸标准")),
    ("机箱支持电源标准缺失", p => Set(p,"case","supportedPsuForms",""), r => Unknown(r,"机箱电源尺寸标准")),
    ("电源SFXL格式规范化", p => {Set(p,"psu","formFactor","SFX-L");Set(p,"case","supportedPsuForms","SFX, SFX L");}, r => Is(r.Findings.Any(f=>f.Title=="电源尺寸标准适配机箱"),Describe(r))),
    ("电源超过机箱限长", p => Set(p,"psu","lengthMm","240"), r => Error(r,"电源超过机箱限长")),
    ("电源长度未知", p => Set(p,"psu","lengthMm","0"), r => Unknown(r,"电源长度")),
    ("机箱电源限长未知", p => Set(p,"case","maxPsuMm","0"), r => Unknown(r,"电源长度")),
    ("电源线材余量小提示", p => Set(p,"psu","lengthMm","195"), r => Is(r.Findings.Any(f=>f.Title=="电源走线余量较小"),Describe(r))),
    ("风冷超高", p => Set(p,"cooler","heightMm","180"), r => Error(r,"散热器超过")),
    ("未知散热器类型", p => p.Parts["cooler"].Specs.Remove("radiatorMm"), r => Unknown(r,"散热器类型")),
    ("水冷不支持", p => Set(p,"cooler","radiatorMm","420"), r => Error(r,"机箱不支持所选冷排")),
    ("冷排没有安装位资料", p => {Set(p,"cooler","radiatorMm","360");Set(p,"case","supportedRadiators","");}, r => Unknown(r,"水冷冷排")),
    ("水冷安装位匹配", p => Set(p,"cooler","radiatorMm","360"), r => Is(r.Findings.Any(f=>f.Title=="冷排规格有对应安装位"),Describe(r))),
    ("散热器扣具错误", p => Set(p,"cooler","sockets","LGA1700"), r => Error(r,"散热器扣具")),
    ("电源额定功率不足", p => Set(p,"psu","powerWatts","300"), r => Error(r,"电源功率不足")),
    ("CPU功耗未知不提供伪估算", p => p.Parts["cpu"].Specs.Remove("powerWatts"), r => {Unknown(r,"整机功耗");Is(r.EstimatedWatts==0&&r.RecommendedPsu==0,Describe(r));}),
    ("仅TDP功耗不能优秀", p => Set(p,"cpu","powerBasis","TDP"), r => Unknown(r,"CPU 最大功耗")),
    ("GPU功耗未知不提供伪估算", p => p.Parts["gpu"].Specs.Remove("powerWatts"), r => {Unknown(r,"整机功耗");Is(r.EstimatedWatts==0&&r.RecommendedPsu==0,Describe(r));}),
    ("电源功率未知", p => p.Parts["psu"].Specs.Remove("powerWatts"), r => Unknown(r,"电源额定功率")),
    ("无穷功率视为未知", p => Set(p,"cpu","powerWatts","Infinity"), r => Unknown(r,"整机功耗")),
    ("未知部分功耗但已知功耗已超电源", p => {Set(p,"cpu","powerWatts","500");p.Parts["gpu"].Specs.Remove("powerWatts");Set(p,"psu","powerWatts","300");}, r => Error(r,"电源低于已知")),
    ("无核显无独显", p => {p.Parts.Remove("gpu");Set(p,"cpu","igpu","false");}, r => Error(r,"没有可用的图形输出")),
    ("未知核显无独显", p => {p.Parts.Remove("gpu");p.Parts["cpu"].Specs.Remove("igpu");}, r => Unknown(r,"显示输出")),
    ("办公核显完整配置", p => {p.Parts.Remove("gpu");p.Usage="办公";}, r => Is(r.Grade=="优秀"&&r.EstimatedWatts==200,Describe(r))),
    ("内存条多于插槽", p => Set(p,"memory","modules","6"), r => Error(r,"内存条数超过")),
    ("内存总量超过上限", p => Set(p,"memory","memoryGb","256"), r => Error(r,"内存容量超过")),
    ("无NVMe接口", p => Set(p,"motherboard","m2Slots","0"), r => Error(r,"主板缺少对应")),
    ("未知硬盘接口不能优秀", p => Set(p,"storage","interface","未知"), r => Unknown(r,"存储接口")),
    ("M2 SATA协议不能推断", p => Set(p,"storage","interface","M.2 SATA"), r => Unknown(r,"M.2 SATA")),
    ("SATA接口缺失", p => {Set(p,"storage","interface","SATA");Set(p,"motherboard","sataPorts","0");}, r => Error(r,"主板缺少对应")),
    ("硬错误优先于不完整", p => {p.Parts.Remove("storage");Set(p,"cpu","socket","LGA1700");}, r => Is(r.Grade=="差",Describe(r))),
    ("预算超支具体说明", p => p.Budget=100, r => Is(r.Findings.Any(f=>f.Title=="超出预算"&&f.Detail.Contains("超支")),Describe(r))),
    ("未验证价格不伪称实时", p => p.Parts.Values.ToList().ForEach(x=>x.Offers.Clear()), r => Unverified(r)),
    ("伪造平台域名不算已核验", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].Url="https://jd.com.evil.example/item"), r => Unverified(r)),
    ("价格不相同不能说核验", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].Price=1), r => Unverified(r)),
    ("未来商品时间不能算近期", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].CheckedAt=DateTimeOffset.Now.AddDays(2)), r => Unverified(r)),
    ("采用候选报价认可用户型号核对而不证明库存", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].MatchStatus="用户已核对型号 / 库存待核验"), r => {Is(!r.Findings.Any(f=>f.Title=="商品报价未全部核对"),Describe(r));Is(r.Findings.Any(f=>f.Title=="库存与到手价需在下单时确认"),Describe(r));}),
    ("手动记录链接可作为用户报价记录", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].MatchStatus="用户已核对 / 手动记录"), r => Is(!r.Findings.Any(f=>f.Title=="商品报价未全部核对"),Describe(r))),
    ("关键词候选不能认定核价", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].MatchStatus="关键词候选，型号/库存待核对"), r => Unverified(r)),
    ("搜索入口不能作为具体已核价商品", p => p.Parts.Values.ToList().ForEach(x=>x.Offers[0].Url="https://search.jd.com/Search?keyword=CPU"), r => Unverified(r)),
    ("淘宝详情商品可记录报价", p => p.Parts.Values.ToList().ForEach(x=>{x.Offers[0].Url="https://item.taobao.com/item.htm?id=123456";x.Offers[0].Platform="淘宝";}), r => Is(!r.Findings.Any(f=>f.Title=="商品报价未全部核对"),Describe(r))),
    ("未知档位只明确未判断性能", p => {p.Parts["cpu"].Specs.Remove("cpuTier");p.Parts["gpu"].Specs.Remove("gpuTier");}, r => Is(r.Findings.Any(f=>f.Title=="性能平衡尚未判断"&&f.Level=="info")&&!r.Findings.Any(f=>f.Title=="CPU 与显卡档位较均衡"),Describe(r))),
    ("显著档位失衡只作经验判断", p => {Set(p,"cpu","cpuTier","1");Set(p,"gpu","gpuTier","5");}, r => Is(r.Findings.Any(f=>f.Title.Contains("档位相差")&&f.Detail.Contains("不是跑分")),Describe(r)))
};
var failures=0;
foreach(var (name,change,check) in tests)
{
    try {var plan=Complete();change(plan);check(RatingEngine.Evaluate(plan));Console.WriteLine("PASS " + name);}
    catch(Exception ex){failures++;Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);}
}
Console.WriteLine($"{tests.Length-failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static BuildPlan Complete()
{
    var p = new BuildPlan {Budget=20000,Usage="2K 游戏"};
    void Add(string category,params (string Key,string Value)[] specs)
    {
        p.Parts[category]=new Part {Category=category,Name="Test " + category,Price=500,PriceKind="已核验",Specs=specs.ToDictionary(x=>x.Key,x=>x.Value),Offers=new() {new Offer {Platform="京东",Price=500,Url="https://item.jd.com/123456.html",MatchStatus="已核验",CheckedAt=DateTimeOffset.Now}}};
    }
    Add("cpu",("socket","AM5"),("memoryType","DDR5"),("igpu","true"),("powerWatts","120"),("cpuTier","3"));
    Add("motherboard",("socket","AM5"),("memoryType","DDR5"),("formFactor","M-ATX"),("ramSlots","4"),("maxMemoryGb","128"),("m2Slots","2"),("sataPorts","4"));
    Add("gpu",("lengthMm","280"),("powerWatts","200"),("gpuTier","3"));
    Add("memory",("memoryType","DDR5"),("memoryGb","32"),("modules","2"),("memorySpeed","6000"));
    Add("storage",("storageGb","1000"),("interface","M.2 NVMe"));
    Add("cooler",("sockets","AM4,AM5,LGA1700"),("heightMm","155"),("radiatorMm","0"),("coolerWatts","220"));
    Add("psu",("powerWatts","750"),("formFactor","ATX"),("lengthMm","150"));
    Add("case",("supportedForms","ATX,Micro ATX,Mini-ITX"),("maxGpuMm","350"),("maxCoolerMm","165"),("supportedRadiators","120,240,280,360"),("supportedPsuForms","ATX,SFX,SFX-L"),("maxPsuMm","200"));
    return p;
}
static void Set(BuildPlan p,string category,string key,string value) => p.Parts[category].Specs[key]=value;
static void Is(bool pass,string detail) {if(!pass)throw new Exception(detail);}
static string Describe(RatingResult r) => r.Grade+"/"+r.Score+": "+string.Join(" | ",r.Findings.Select(f=>f.Title));
static void Error(RatingResult r,string title) {Is(r.Grade=="差"&&r.Score<=49&&r.Findings.Any(f=>f.Level=="error"&&f.Title.Contains(title)),Describe(r));}
static void Unknown(RatingResult r,string title) {Is(r.Grade!="优秀"&&r.Findings.Any(f=>f.Level=="warn"&&f.Title.Contains(title)&&f.Title.Contains("待核验")),Describe(r));}
static void Unverified(RatingResult r) => Is(r.Findings.Any(f=>f.Title=="商品报价未全部核对"),Describe(r));
