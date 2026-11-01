using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using MemoDesk.Platform.Windows.Security;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.App.Views;
using MemoDesk.Infrastructure;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Vault.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MemoDesk.Vault.UiProbe;

internal static class Program
{
    private const string Master = "Sample2026";
    private static readonly List<object> Checks = [];
    private static string Output = "";
    private static int _exitCode;
    private static bool _nativeClipboard;
    private static bool _nativeClipboardRestored;
    [STAThread] private static int Main(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        _nativeClipboard = args.Contains("--native-clipboard", StringComparer.Ordinal);
        if (!File.Exists(Path.Combine(root, "MemoDesk.sln"))) throw new InvalidOperationException("Run from the repository root.");
        Output = Path.Combine(root, "artifacts", "vault-validation", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load(Path.Combine(root, "src/MemoDesk.App/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration),
            xml.Root.Element(ns + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        var text = dictionary.ToString();
        foreach (var name in new[] { "Converters", "ViewModels", "Views" })
            text = text.Replace("clr-namespace:MemoDesk.App." + name, "clr-namespace:MemoDesk.App." + name + ";assembly=MemoDesk.App", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)XamlReader.Parse(text);
        AppTheme.Start();
        var clipboard = new ProbeClipboard();
        var composition = typeof(App.App).GetMethod("BuildHost", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var host = (IHost)composition.Invoke(null, [new AppPaths(Path.Combine(Output, "isolated-data")), clipboard])!;
        host.Services.GetRequiredService<DatabaseBootstrapper>().Initialize();
        var vm = host.Services.GetRequiredService<VaultViewModel>();
        var main = host.Services.GetRequiredService<App.MainWindow>();
        main.Title = "[隔离验收] MemoDesk 密码库"; main.Width = 1120; main.Height = 780;
        var nav = host.Services.GetRequiredService<MainViewModel>(); nav.NavigateTo("vault");
        vm.Locks.Attach(main); vm.Locks.Start();
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Scenarios(app, main, vm, clipboard, host);
                Console.WriteLine($"Passed {Checks.Count} Windows UI checks. Artifacts: {Output}");
            }
            catch (Exception ex)
            {
                _exitCode = 1; Checks.Add(new { Passed = false, Error = ex.ToString(), vm.Status, vm.IsBusy, vm.IsUnlocked }); Console.WriteLine(ex); Console.WriteLine(vm.Status);
            }
            finally
            {
                File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(new
                {
                    ProductionViews = true, ProductionComposition = true, ProductionAppLifecycle = false,
                    AiWorkersStarted = false, NativeClipboardTested = _nativeClipboard,
                    PreviousClipboardRestored = _nativeClipboardRestored,
                    Clipboard = _nativeClipboard ? "Synthetic native clipboard check; restore only when clipboard still belongs to test" : "Isolated fake; native clipboard not modified", Checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                vm.Locks.Dispose();
                foreach (var window in app.Windows.OfType<Window>().ToArray()) window.Close();
                app.Shutdown();
            }
        };
        app.Run(main); return _exitCode;
    }
    private static async Task Scenarios(Application app, Window main, VaultViewModel vm, ProbeClipboard clipboard, IHost host)
    {
        await Render();
        await Wait(() => !vm.IsBusy);
        var page = Descendants<VaultView>(main).Single();
        var master = (PasswordBox)page.FindName("MasterBox");
        var confirm = (PasswordBox)page.FindName("ConfirmMasterBox");
        var unlock = (Button)page.FindName("UnlockButton");
        main.Width = 960; main.Height = 600; await Render();
        var lockedScroll = Descendants<ScrollViewer>((Grid)page.FindName("LockedPanel")).First();
        lockedScroll.ScrollToBottom(); await Render();
        var restoreButton = Button(page, "从加密备份恢复");
        var bounds = restoreButton.TransformToAncestor(lockedScroll).TransformBounds(new Rect(restoreButton.RenderSize));
        Check("initial-locked", !vm.IsUnlocked && vm.Rows.Count == 0 && vm.IsNew && bounds.Bottom <= lockedScroll.ActualHeight + 1 && bounds.Top >= 0);
        lockedScroll.ScrollToTop(); main.Width = 1120; main.Height = 780; await Render();
        var formError = (TextBlock)page.FindName("MasterErrorText");
        var formErrorPanel = (Border)page.FindName("MasterErrorPanel");
        Check("master-length-hint-matches-policy", Descendants<TextBlock>(page).Any(t => t.Text == MasterPasswordPolicy.Requirements));
        Click(unlock);
        Check("empty-master-is-explained-inline", formErrorPanel.IsVisible && formError.Text == "请输入主密码。" && Equals(master.Tag, "Invalid"));
        master.Password = confirm.Password = "short"; Click(unlock);
        Check("short-master-shows-count-and-keeps-input", formError.Text.Contains("目前只有 5 个，还差 5 个", StringComparison.Ordinal) && master.Password == "short" && confirm.Password == "short" && !vm.IsBusy && !vm.Vault.Exists);
        await Render(); Capture(main, "master-password-validation.png");
        master.Password = confirm.Password = new string('a', 1025); Click(unlock);
        Check("long-master-is-explained-without-truncation", formError.Text.Contains("最多支持 1024", StringComparison.Ordinal) && master.Password.Length == 1025 && !vm.Vault.Exists);
        master.Password = confirm.Password = "123456789"; Click(unlock);
        Check("nine-character-master-is-rejected", formError.Text.Contains("目前只有 9 个，还差 1 个", StringComparison.Ordinal) && !vm.IsBusy && !vm.Vault.Exists);
        master.Password = "abcdefghij"; confirm.Password = "different-password"; Click(unlock);
        Check("confirmation-mismatch-has-specific-feedback", formError.Text.Contains("不一致", StringComparison.Ordinal) && Equals(confirm.Tag, "Invalid") && master.Password == "abcdefghij");
        confirm.Password = master.Password;
        Check("corrected-master-clears-error", formErrorPanel.Visibility == Visibility.Collapsed && master.Tag is null && confirm.Tag is null);
        using (var change = new VaultPasswordWindow("修改主密码", vm, change: true))
            await Dialog<VaultPasswordWindow>(app, () => { change.Owner = main; change.ShowDialog(); }, async prompt =>
            {
                var current = Field<PasswordBox>(prompt, "_current"); var next = Field<PasswordBox>(prompt, "_password"); var repeated = Field<PasswordBox>(prompt, "_confirm");
                current.Password = Master; next.Password = repeated.Password = "short"; Click(Button(prompt, "确认"));
                var error = (TextBlock)typeof(VaultDialogWindow).GetField("Error", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(prompt)!;
                Check("change-master-explains-short-password", prompt.IsVisible && error.Text.Contains("还差 5 个", StringComparison.Ordinal) && next.Password == "short");
                next.Password = repeated.Password = new string('a', 1025); Click(Button(prompt, "确认"));
                Check("change-master-does-not-truncate-long-input", next.Password.Length == 1025 && error.Text.Contains("最多支持 1024", StringComparison.Ordinal));
                next.Password = "abcdefghijkl"; repeated.Password = "different-password"; Click(Button(prompt, "确认"));
                Check("change-master-distinguishes-mismatch", prompt.IsVisible && error.Text.Contains("不一致", StringComparison.Ordinal));
                prompt.Close(); await Task.CompletedTask;
            });
        master.Password = confirm.Password = Master; Click(unlock);
        await Wait(() => !vm.IsBusy && vm.IsUnlocked);
        Check("create-clears-password-inputs", master.Password.Length == 0 && confirm.Password.Length == 0);
        await Dialog<VaultEditWindow>(app, () => Click((Button)page.FindName("NewEntryButton")), async edit =>
        {
            Field<TextBox>(edit, "_title").Text = "GitHub · 示例账号";
            Field<TextBox>(edit, "_user").Text = "developer@example.invalid";
            Field<PasswordBox>(edit, "_password").Password = "Synthetic-UI-secret-2026!";
            Field<TextBox>(edit, "_url").Text = "https://github.com";
            Field<TextBox>(edit, "_category").Text = "工作";
            Field<TextBox>(edit, "_notes").Text = "这是隔离验收用的合成记录。";
            await Render(); Capture(edit, "editor.png");
            Click(Button(edit, "保存账号")); await Wait(() => !edit.IsVisible);
        });
        await Wait(() => !vm.IsBusy);
        Check("production-editor-save", vm.Rows.Count == 1);
        using (var d = new VaultEntryDraft { Title = "工作邮箱 · 示例", UserName = "mail@example.invalid", Password = "Synthetic-mail-2026!", Category = "工作" }) vm.Vault.Save(d);
        using (var d = new VaultEntryDraft { Title = "内网 VPN · 示例", UserName = "sample-user", Password = "Synthetic-vpn-2026!", Category = "个人" }) vm.Vault.Save(d);
        vm.Refresh(); await Wait(() => !vm.IsBusy);
        vm.Query = "GitHub"; Check("metadata-search", vm.Rows.Count == 1); vm.SelectedEntry = vm.Rows[0];
        Click(Button(page, "显示 10 秒"));
        Check("explicit-reveal", ((TextBlock)page.FindName("RevealedPassword")).Text == "Synthetic-UI-secret-2026!");
        vm.Query = "内网"; Check("selection-clears-reveal", ((TextBlock)page.FindName("RevealedPassword")).Text == "••••••••••••");
        vm.Query = ""; vm.SelectedEntry = vm.Rows.Single(r => r.Title.Contains("GitHub", StringComparison.Ordinal));
        await Render(); Capture(main, "vault-unlocked.png");
        await Dialog<VaultEditWindow>(app, () => Click(Button(page, "编辑")), edit =>
        {
            var password = Field<PasswordBox>(edit, "_password"); vm.Locks.Lock();
            Check("lock-closes-and-scrubs-editor", !edit.IsVisible && password.Password.Length == 0 && vm.Rows.Count == 0 && !vm.IsUnlocked);
            return Task.CompletedTask;
        });
        await Render(); Capture(main, "vault-locked.png");
        master.Password = "wrong-password"; Click(unlock); await Wait(() => !vm.IsBusy);
        Check("wrong-master-is-explained-near-input", formErrorPanel.IsVisible && formError.Text.Contains("主密码不正确", StringComparison.Ordinal) && !formError.Text.Contains("长度", StringComparison.Ordinal));
        await Unlock(); main.Hide(); Check("hide-locks-vault", !vm.Vault.IsUnlocked && vm.Rows.Count == 0); main.Show(); main.Activate();
        await Unlock(); main.WindowState = WindowState.Minimized;
        Check("minimize-locks-vault", !vm.Vault.IsUnlocked); main.WindowState = WindowState.Normal; main.Activate();
        await Unlock();
        var quick = new VaultQuickWindow(vm); quick.Show(); await Render();
        Field<TextBox>(quick, "_query").Text = "GitHub";
        Click(Button(quick, "复制密码"));
        Check("quick-search-copies-selected-secret", clipboard.Value == "Synthetic-UI-secret-2026!" && !quick.IsVisible);
        vm.Locks.Lock(); Check("lock-clears-owned-clipboard", clipboard.Value.Length == 0);
        await Unlock();
        var generator = new VaultGeneratorWindow(vm); generator.Show(); await Render();
        var result = Field<TextBox>(generator, "_result");
        Check("generator-actual-window", result.Text.Length == 24);
        var unsafeCopies = 0;
        // Stop any regression from touching the user's clipboard while testing routed Copy commands.
        System.Windows.DataObject.AddCopyingHandler(result, (_, e) => { unsafeCopies++; e.CancelCommand(); });
        var generated = result.Text;
        result.SelectAll(); ApplicationCommands.Copy.Execute(null, result);
        Check("generator-standard-copy-uses-secure-service", clipboard.Value == generated && unsafeCopies == 0);
        result.Select(2, 8); ApplicationCommands.Copy.Execute(null, result);
        Check("generator-selection-copy-uses-secure-service", clipboard.Value == generated.Substring(2, 8) && unsafeCopies == 0);
        result.Select(0, 0);
        Check("generator-copy-disabled-without-selection", !ApplicationCommands.Copy.CanExecute(null, result));
        Click(Button(generator, "复制密码"));
        Check("generator-button-copies-entire-password", clipboard.Value == generated);
        clipboard.RejectCopies = true; result.SelectAll(); ApplicationCommands.Copy.Execute(null, result);
        Check("generator-copy-failure-is-visible", Descendants<TextBlock>(generator).Any(t => t.Text.Contains("复制失败", StringComparison.Ordinal)) && unsafeCopies == 0);
        clipboard.RejectCopies = false;
        clipboard.AfterCopy = () => vm.Locks.Lock();
        ApplicationCommands.Copy.Execute(null, result); clipboard.AfterCopy = null;
        Check("generator-lock-during-copy-clears-clipboard", clipboard.Value.Length == 0);
        Check("lock-scrubs-generator", !generator.IsVisible && result.Text.Length == 0);
        Check("generator-copy-disabled-after-lock", !ApplicationCommands.Copy.CanExecute(null, result) && unsafeCopies == 0);
        await Unlock();
        var csvPath = Path.Combine(Output, "synthetic-import.csv");
        File.WriteAllText(csvPath, "name,username,password,url\nCSV 示例,import@example.invalid,Synthetic-import-2026!,https://example.invalid\n");
        using (var csv = VaultCsv.Read(csvPath))
            await Dialog<VaultImportWindow>(app, () => new VaultImportWindow(vm, csv) { Owner = main }.ShowDialog(), async import =>
            {
                Click(Button(import, "预览导入")); Check("csv-mapping-preview", Button(import, "确认导入").IsEnabled);
                Click(Button(import, "确认导入")); await Wait(() => !import.IsVisible);
            });
        await Wait(() => !vm.IsBusy); vm.Filter = "全部"; vm.Query = "";
        Check("csv-import-through-window", vm.Rows.Count == 4);
        var settings = new VaultSettingsWindow(vm); settings.Show(); await Render();
        Field<ComboBox>(settings, "_minutes").SelectedItem = 1; Click(Button(settings, "保存设置"));
        Check("idle-setting-persists", vm.Settings.VaultIdleMinutes == 1);
        var oldGesture = vm.Hotkeys.Gesture;
        Check("hotkey-register", vm.Hotkeys.Apply("Ctrl+Alt+Shift+F24") is null);
        Check("invalid-hotkey-preserves-registration", vm.Hotkeys.Apply("invalid") is not null && vm.Hotkeys.Gesture == "Ctrl+Alt+Shift+F24");
        _ = oldGesture;
        using (var c = host.Services.GetRequiredService<SqliteConnectionFactory>().Create())
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT (SELECT count(*) FROM captures)+(SELECT count(*) FROM items)+(SELECT count(*) FROM timeline_events)";
            Check("vault-never-creates-memos-or-timeline", Convert.ToInt64(cmd.ExecuteScalar()) == 0);
        }
        var capture = host.Services.GetRequiredService<QuickCaptureWindow>();
        var routed = false; capture.VaultRequested += () => routed = true;
        main.Activate(); capture.ShowForCapture(); await Render();
        var captureText = (TextBox)capture.FindName("InputBox"); captureText.Text = "synthetic-not-submitted";
        Click(Button(capture, "账号和密码请使用密码库"));
        Check("quick-capture-vault-route-discards-unsaved-text", routed && captureText.Text.Length == 0 && !capture.IsVisible);
        vm.Locks.Lock();
        var version = vm.Vault.SessionVersion;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var pending = vm.RunAsync(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); vm.Vault.Unlock(Master, version); });
        await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))); vm.Locks.Lock(); release.Set(); await pending;
        Check("late-unlock-cannot-reopen-locked-ui", !vm.IsUnlocked && !vm.Vault.IsUnlocked && vm.Rows.Count == 0);
        await Unlock();
        var backup = Path.Combine(Output, "ui-transfer.memovault"); vm.Vault.ExportBackup(backup);
        var restoreVersion = vm.Vault.SessionVersion;
        var restored = await vm.RunAsync(() => vm.Vault.RestoreBackup(backup, Master, restoreVersion), "恢复完成", changesSession: true);
        Check("restore-reports-success-and-requires-unlock", restored && !vm.IsUnlocked && vm.Status == "恢复完成");
        if (_nativeClipboard) await NativeClipboardCheck();
        async Task Unlock()
        {
            master.Password = Master; Click(unlock); await Wait(() => !vm.IsBusy && vm.IsUnlocked);
        }
    }
    private static async Task NativeClipboardCheck()
    {
        // Keep the previous IDataObject opaque: never print, serialize or inspect its contents.
        var original = System.Windows.Clipboard.GetDataObject();
        var clock = new ClipboardClock(); using var clipboard = new SecureClipboard(clock);
        uint sequence = 0;
        try
        {
            Check("native-clipboard-copy", clipboard.Copy("MemoDesk synthetic clipboard value"));
            sequence = GetClipboardSequenceNumber();
            Check("native-clipboard-text", System.Windows.Clipboard.GetText() == "MemoDesk synthetic clipboard value");
            foreach (var format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            {
                using var data = System.Windows.Clipboard.GetData(format) as MemoryStream;
                Check("native-" + format, data is not null && data.Length == 4 && data.ToArray().All(b => b == 0));
            }
            System.Windows.Clipboard.SetText("MemoDesk synthetic replacement"); sequence = GetClipboardSequenceNumber();
            clipboard.ClearOwned();
            Check("native-do-not-clear-replacement", System.Windows.Clipboard.GetText() == "MemoDesk synthetic replacement");
            Check("native-second-copy", clipboard.Copy("MemoDesk synthetic expiry")); sequence = GetClipboardSequenceNumber();
            clock.Ticks = TimeSpan.FromSeconds(21).Ticks;
            await Wait(() => !System.Windows.Clipboard.ContainsText()); sequence = GetClipboardSequenceNumber();
            Check("native-20-second-expiry", !System.Windows.Clipboard.ContainsText());
        }
        finally
        {
            if (sequence == GetClipboardSequenceNumber())
            {
                clipboard.ClearOwned();
                if (original is not null) System.Windows.Clipboard.SetDataObject(original, copy: true);
                else System.Windows.Clipboard.Clear();
                _nativeClipboardRestored = true;
            }
        }
    }
    private sealed class ClipboardClock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private static void Check(string name, bool passed)
    { Checks.Add(new { Name = name, Passed = passed }); if (!passed) throw new InvalidOperationException(name); }
    private static async Task Wait(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition()) { if (sw.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("UI wait timed out"); await Task.Delay(15); }
    }
    private static async Task Render() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(60); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static Button Button(DependencyObject root, string label) => Descendants<Button>(root).First(b => b.Content as string == label);
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
    private static async Task Dialog<T>(Application app, Action open, Func<T, Task> steps) where T : Window
    {
        var done = new TaskCompletionSource();
        _ = app.Dispatcher.BeginInvoke(async () =>
        {
            try { await steps(app.Windows.OfType<T>().Single()); done.SetResult(); }
            catch (Exception ex) { foreach (var w in app.Windows.OfType<T>().ToArray()) w.Close(); done.SetException(ex); }
        }, DispatcherPriority.ApplicationIdle);
        open(); await done.Task;
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(surface.RenderSize));
            context.DrawRectangle(window.Background, null, new Rect(surface.RenderSize));
        }
        bitmap.Render(backdrop);
        bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Output, name)); encoder.Save(file);
    }
    private sealed class ProbeClipboard : ISecureClipboard
    {
        public string Value { get; private set; } = "";
        public bool RejectCopies { get; set; }
        public Action? AfterCopy { get; set; }
        public bool Copy(string value)
        {
            if (RejectCopies) return false;
            Value = value; AfterCopy?.Invoke(); return true;
        }
        public void ClearOwned() => Value = "";
    }
}
