using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using GalgameManager.Application;
using GalgameManager.Infrastructure;

namespace GalgameManager.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private bool ownsMutex;
    private readonly HttpClient metadataHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalgameManager");
        if (e.Args is ["--data-directory", var isolatedDirectory])
        {
            try { directory = LocalPaths.Normalize(isolatedDirectory); }
            catch (LibraryException ex)
            {
                MessageBox.Show(ex.Message, "樱笺 · Galgame 管理器", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        var libraryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LocalPaths.Key(directory))));
        mutex = new Mutex(false, $@"Local\GalgameManager.Library.{libraryKey}");
        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            MessageBox.Show("游戏库已经打开，请返回现有窗口。", "樱笺 · Galgame 管理器", MessageBoxButton.OK);
            Shutdown();
            return;
        }

        var log = new DiagnosticLog(directory);
        var store = new SqliteLibrary(Path.Combine(directory, "library.db"), log);
        var files = new MetadataFiles(directory);
        metadataHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GalgameManager/0.2.1 (Windows desktop)");
        var catalog = new MetadataCatalog(metadataHttp, files);
        var service = new LibraryService(store, store, new FileScanner(log), new WindowsGameLauncher(log), files);
        var viewModel = new MainViewModel(service, store.Initialize);
        var window = new MainWindow(viewModel, files, catalog);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex)
            mutex?.ReleaseMutex();
        mutex?.Dispose();
        metadataHttp.Dispose();
        base.OnExit(e);
    }
}
