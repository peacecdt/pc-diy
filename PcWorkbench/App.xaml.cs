using System.Windows;
using System.IO;
namespace PcWorkbench;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) => { try { Directory.CreateDirectory(StorageService.Root); File.AppendAllText(Path.Combine(StorageService.Root, "error.log"), DateTimeOffset.Now + " " + ev.Exception + Environment.NewLine); } catch { } MessageBox.Show("操作未完成：" + ev.Exception.Message, "装机工坊"); ev.Handled = true; };
        new MainWindow(e.Args).Show();
    }
}
