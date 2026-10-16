using System.Net;
using System.Text;
using System.Text.Json;
using PcWorkbench;

static void Check(bool ok,string msg) {if(!ok)throw new Exception("FAIL: "+msg);Console.WriteLine("PASS: "+msg);}
var json = "{\"jd_union_open_goods_query_responce\":{\"code\":\"0\",\"queryResult\":" + JsonSerializer.Serialize("{\"code\":200,\"data\":[{\"skuName\":\"AMD Ryzen 7 9800X3D\",\"materialUrl\":\"item.jd.com/123.html\",\"priceInfo\":{\"price\":2999.9},\"shopInfo\":{\"shopName\":\"品牌旗舰店\"}}]}") + "}}";
var jd=OnlineService.ParseJdOffers(json,DateTimeOffset.UtcNow);
Check(jd.Count==1&&jd[0].Price==2999.9m&&jd[0].Url=="https://item.jd.com/123.html"&&jd[0].MatchStatus.Contains("待核对"),"京东嵌套字符串/价格/URL/待核对标签");
var tb=OnlineService.ParseTaobaoOffers("""{"tbk_dg_material_optional_upgrade_response":{"result_list":{"map_data":[{"item_basic_info":{"title":"内存 32GB","shop_title":"测试店","user_type":1},"publish_info":{"click_url":"//s.click.taobao.com/abc"},"price_promotion_info":{"zk_final_price":"599.00","final_promotion_price":"499"}}]}}}""",DateTimeOffset.UtcNow);
Check(tb.Count==1&&tb[0].Price==599m&&tb[0].Platform=="天猫"&&tb[0].Url.StartsWith("https://"),"淘宝升级版嵌套schema，取销售价不取条件优惠价");
Check(OnlineService.MarketplaceUrl("javascript:alert(1)","jd.com")==""&&OnlineService.MarketplaceUrl("https://jd.com.evil.example/","jd.com")=="","拒绝非HTTP和伪造电商域名");
try {OnlineService.ParseTaobaoOffers("""{"error_response":{"code":15,"msg":"签名错误"}}""",DateTimeOffset.UtcNow);throw new Exception("error not thrown");}catch(InvalidDataException){Console.WriteLine("PASS: 淘宝API错误可见");}
Check(OnlineService.SignMd5(new Dictionary<string,string>{{"b","2"},{"a","1"}},"secret")==Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("secreta1b2secret"))),"官方MD5参数排序/前后secret");
var fixturePath = Path.Combine(AppContext.BaseDirectory,"fixture-"+Guid.NewGuid().ToString("N")+".zip");
using(var fixtureStream=File.Create(fixturePath))
using(var fixture=new System.IO.Compression.ZipArchive(fixtureStream,System.IO.Compression.ZipArchiveMode.Create))
{
 foreach(var category in new[]{"CPU","GPU","Motherboard","RAM","Storage","CPUCooler","PSU","PCCase"})
 {
  var id=Guid.NewGuid().ToString(); var entry=fixture.CreateEntry("fixture-main/open-db/"+category+"/"+id+".json");
  using var writer=new StreamWriter(entry.Open()); writer.Write(JsonSerializer.Serialize(new{opendb_id=id,metadata=new{name="Fixture "+category,manufacturer="Fixture"}}));
 }
}
List<Part> p;
try { p=await OnlineService.ParseArchiveAsync(fixturePath,DateTimeOffset.UtcNow,new Progress<string>(Console.WriteLine),CancellationToken.None); }
finally {File.Delete(fixturePath);}
Check(p.Select(x=>x.Category).Distinct().Count()==8,"OpenDB格式ZIP八类解析");
Check(p.All(x=>x.Price==null&&x.Offers.Count==0&&!x.Specs.ContainsKey("cpuTier")&&!x.Specs.ContainsKey("gpuTier")),"硬件导入无伪造电商价格/性能评分");
Console.WriteLine("TOTAL "+p.Count+" "+string.Join(",",p.GroupBy(x=>x.Category).Select(g=>g.Key+"="+g.Count())));
Console.WriteLine(JsonSerializer.Serialize(p.First(x=>x.Category=="motherboard")));
using(var noKeys=new OnlineService()) Check((await noKeys.SearchOffersAsync("Ryzen",new AppSettings(),new Progress<string>(Console.WriteLine))).Count==0,"无凭据友好返回而不伪造商品");
var messages=new List<string>();
using(var transport=new HttpClient(new MockHandler()))
using(var svc=new OnlineService(transport,Path.Combine(AppContext.BaseDirectory,"mock-cache")))
{
 var offers=await svc.SearchOffersAsync("Ryzen",new AppSettings {JdAppKey="fake-jd-key",JdSecret="test-secret",TaobaoAppKey="fake-tb-key",TaobaoSecret="test-secret",TaobaoAdzoneId="1"},new Capture(messages));
 Check(offers.Count==1&&messages.Any(x=>x.Contains("京东联盟查询失败"))&&messages.All(x=>!x.Contains("test-secret")),"单平台失败不丢弃另一平台结果且日志隐藏密钥");
 using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
 try{await svc.SearchOffersAsync("Ryzen",new AppSettings {JdAppKey="x",JdSecret="y"},null,cancelled.Token);throw new Exception("not cancelled");}catch(OperationCanceledException){Console.WriteLine("PASS: 用户取消传播");}
}
using(var transport=new HttpClient(new MockHandler(true)))
using(var svc=new OnlineService(transport,Path.Combine(AppContext.BaseDirectory,"mock-cache")))
{
 try{await svc.SearchOffersAsync("Ryzen",new AppSettings{JdAppKey="fake",JdSecret="test-secret",TaobaoAppKey="fake",TaobaoSecret="test-secret",TaobaoAdzoneId="1"});throw new Exception("not failed");}catch(InvalidDataException ex){Check(ex.Message.Contains("京东联盟查询失败")&&ex.Message.Contains("淘宝联盟查询失败")&&!ex.Message.Contains("test-secret"),"双平台失败合并错误，密钥不泄露");}
}
if(args.Contains("--live")) {using var svc=new OnlineService();var live=await svc.UpdateHardwareAsync(new Progress<string>(Console.WriteLine));Check(live.Count>10000,"真实HTTPS硬件更新");var again=await svc.UpdateHardwareAsync(new Progress<string>(Console.WriteLine));Check(again.Count==live.Count,"ETag二次更新");}

sealed class Capture(List<string> messages):IProgress<string>{public void Report(string value)=>messages.Add(value);}
sealed class MockHandler(bool allFail=false):HttpMessageHandler
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  var json=request.RequestUri!.Host=="api.jd.com"||allFail?"""{"error_response":{"code":29,"msg":"Invalid test-secret"}}""":"""{"tbk_dg_material_optional_upgrade_response":{"result_list":{"map_data":[{"item_basic_info":{"title":"测试商品","shop_title":"测试店","user_type":1},"publish_info":{"click_url":"//s.click.taobao.com/abc"},"price_promotion_info":{"zk_final_price":"599.00"}}]}}}""";
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
 }
}
