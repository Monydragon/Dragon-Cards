using System.Runtime.InteropServices;
using System.Text;

namespace DragonCards.Desktop;

internal static class DesktopClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static bool TrySetText(string text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "There is no invite code to copy.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return TrySetSdlText(text, out error);
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            error = "The clipboard is busy. Try Copy Code again.";
            return false;
        }

        IntPtr memory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                error = "Could not clear the clipboard. Try Copy Code again.";
                return false;
            }

            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
            if (memory == IntPtr.Zero)
            {
                error = "Could not allocate clipboard memory.";
                return false;
            }

            var destination = GlobalLock(memory);
            if (destination == IntPtr.Zero)
            {
                error = "Could not prepare the clipboard text.";
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, destination, bytes.Length);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                error = "Could not copy the invite code. Try again.";
                return false;
            }

            memory = IntPtr.Zero; // The clipboard owns this allocation after SetClipboardData succeeds.
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero)
            {
                GlobalFree(memory);
            }

            CloseClipboard();
        }
    }

    public static bool TryGetText(out string text, out string error)
    {
        text = "";
        error = "";
        if (!OperatingSystem.IsWindows())
        {
            return TryGetSdlText(out text, out error);
        }

        if (!IsClipboardFormatAvailable(CfUnicodeText))
        {
            error = "The clipboard does not contain text.";
            return false;
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            error = "The clipboard is busy. Try Paste Code again.";
            return false;
        }

        IntPtr memory = IntPtr.Zero;
        IntPtr source = IntPtr.Zero;
        try
        {
            memory = GetClipboardData(CfUnicodeText);
            if (memory == IntPtr.Zero)
            {
                error = "Could not read clipboard text.";
                return false;
            }

            source = GlobalLock(memory);
            if (source == IntPtr.Zero)
            {
                error = "Could not read clipboard text.";
                return false;
            }

            text = Marshal.PtrToStringUni(source)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "The clipboard does not contain an invite code.";
                return false;
            }

            return true;
        }
        finally
        {
            if (source != IntPtr.Zero)
            {
                GlobalUnlock(memory);
            }

            CloseClipboard();
        }
    }

    private static bool TrySetSdlText(string text, out string error)
    {
        error = "";
        try
        {
            var result = OperatingSystem.IsLinux()
                ? LinuxSetClipboardText(text)
                : OperatingSystem.IsMacOS()
                    ? MacSetClipboardText(text)
                    : -1;
            if (result == 0)
            {
                return true;
            }

            error = "The SDL clipboard is unavailable in this desktop session.";
            return false;
        }
        catch (DllNotFoundException)
        {
            error = "The SDL clipboard library is unavailable in this desktop session.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "This SDL runtime does not provide clipboard support.";
            return false;
        }
    }

    private static bool TryGetSdlText(out string text, out string error)
    {
        text = "";
        error = "";
        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = OperatingSystem.IsLinux()
                ? LinuxGetClipboardText()
                : OperatingSystem.IsMacOS()
                    ? MacGetClipboardText()
                    : IntPtr.Zero;
            if (memory == IntPtr.Zero)
            {
                error = "The clipboard does not contain text, or SDL clipboard access is unavailable.";
                return false;
            }

            text = Marshal.PtrToStringUTF8(memory)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "The clipboard does not contain an invite code.";
                return false;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            error = "The SDL clipboard library is unavailable in this desktop session.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "This SDL runtime does not provide clipboard support.";
            return false;
        }
        finally
        {
            if (memory != IntPtr.Zero)
            {
                if (OperatingSystem.IsLinux()) LinuxFree(memory);
                else if (OperatingSystem.IsMacOS()) MacFree(memory);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetClipboardText")]
    private static extern int LinuxSetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetClipboardText")]
    private static extern IntPtr LinuxGetClipboardText();

    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_free")]
    private static extern void LinuxFree(IntPtr memory);

    [DllImport("libSDL2.dylib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetClipboardText")]
    private static extern int MacSetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport("libSDL2.dylib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetClipboardText")]
    private static extern IntPtr MacGetClipboardText();

    [DllImport("libSDL2.dylib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_free")]
    private static extern void MacFree(IntPtr memory);
}
