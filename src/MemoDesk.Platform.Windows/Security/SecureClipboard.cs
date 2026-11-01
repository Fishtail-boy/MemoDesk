using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MemoDesk.Vault.Core;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using Clipboard = System.Windows.Clipboard;

namespace MemoDesk.Platform.Windows.Security;

/// <summary>STA 剪贴板：拒绝系统历史/云同步，按拥有的序号原子清理。</summary>
public sealed partial class SecureClipboard : ISecureClipboard, IDisposable
{
    private readonly DispatcherTimer _timer;
    private uint _sequence;
    private readonly TimeProvider _clock;
    private long _copiedAt;
    public SecureClipboard(TimeProvider clock)
    {
        _clock = clock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (_clock.GetElapsedTime(_copiedAt) >= TimeSpan.FromSeconds(20)) ClearOwned(); };
    }
    public bool Copy(string value)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, value, autoConvert: false);
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, copy: true);
                _sequence = GetClipboardSequenceNumber(); _copiedAt = _clock.GetTimestamp(); _timer.Start(); return true;
            }
            catch (ExternalException) { Thread.Sleep(10); }
        }
        return false;
    }
    public void ClearOwned()
    {
        if (_sequence == 0) { _timer.Stop(); return; }
        if (!OpenClipboard(IntPtr.Zero)) return; // Busy: keep the timer and retry; never clear another owner's data.
        try
        {
            if (GetClipboardSequenceNumber() == _sequence && !EmptyClipboard()) return;
            _sequence = 0; _timer.Stop();
        }
        finally { CloseClipboard(); }
    }
    public void Dispose() { ClearOwned(); _timer.Stop(); GC.SuppressFinalize(this); }
    [LibraryImport("user32.dll")] private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(IntPtr owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
}
