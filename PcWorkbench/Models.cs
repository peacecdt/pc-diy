using System.Text.Json;
namespace PcWorkbench;
public class Part {
 public string Id {get;set;} = Guid.NewGuid().ToString();
 public string Category {get;set;} = "cpu";
 public string Name {get;set;} = "";
 public string Brand {get;set;} = "";
 public decimal? Price {get;set;}
 public string PriceKind {get;set;} = "未报价";
 public string Source {get;set;} = "本地";
 public string SourceUrl {get;set;} = "";
 public DateTimeOffset? UpdatedAt {get;set;}
 public Dictionary<string,string> Specs {get;set;} = new();
 public List<Offer> Offers {get;set;} = new();
 public string S(string key) => Specs.GetValueOrDefault(key, "");
 public double? N(string key) => double.TryParse(S(key), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
 public bool? B(string key) => bool.TryParse(S(key), out var b) ? b : null;
 public override string ToString() => Name;
}
public class Offer {
 public string Platform {get;set;} = "";
 public string Title {get;set;} = "";
 public string Url {get;set;} = "";
 public decimal? Price {get;set;}
 public string Shop {get;set;} = "";
 public DateTimeOffset CheckedAt {get;set;} = DateTimeOffset.Now;
 public string MatchStatus {get;set;} = "待核对型号";
}
public class BuildPlan {
 public string Name {get;set;} = "我的配置";
 public string Usage {get;set;} = "2K 游戏";
 public decimal Budget {get;set;} = 8000;
 public Dictionary<string,Part> Parts {get;set;} = new();
 public DateTimeOffset SavedAt {get;set;} = DateTimeOffset.Now;
}
public class Finding {
 public string Level {get;set;} = "info"; // error / warn / good / info
 public string Title {get;set;} = "";
 public string Detail {get;set;} = "";
}
public class RatingResult {
 public string Grade {get;set;} = "待完善";
 public int Score {get;set;}
 public int EstimatedWatts {get;set;}
 public int RecommendedPsu {get;set;}
 public List<Finding> Findings {get;set;} = new();
}
public class AppSettings {
 public bool AutoUpdate {get;set;} = true;
 public int UpdateHours {get;set;} = 24;
 public DateTimeOffset? LastHardwareSync {get;set;}
 public DateTimeOffset? LastOfferSync {get;set;}
 public string JdAppKey {get;set;} = "";
 public string JdSecret {get;set;} = "";
 public string TaobaoAppKey {get;set;} = "";
 public string TaobaoSecret {get;set;} = "";
 public string TaobaoAdzoneId {get;set;} = "";
}
public static class AppJson {
 public static readonly JsonSerializerOptions Options = new() {PropertyNameCaseInsensitive=true, WriteIndented=true};
}
public static class Categories {
 public static readonly Dictionary<string,string> All = new() {{"cpu","处理器"},{"motherboard","主板"},{"gpu","显卡"},{"memory","内存"},{"storage","固态 / 硬盘"},{"cooler","散热器"},{"psu","电源"},{"case","机箱"}};
}
