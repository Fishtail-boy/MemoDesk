using System.ComponentModel;
using System.Diagnostics;

namespace MemoDesk.App.Services;

internal static class DigitalAssetLinks
{
    public static bool Open(Uri link)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
