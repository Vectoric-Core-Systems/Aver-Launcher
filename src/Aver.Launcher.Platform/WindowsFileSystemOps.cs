using System.Runtime.InteropServices;
using Aver.Launcher.Updater;

namespace Aver.Launcher.Platform;

/// <summary>
/// Hard links and atomic directory renames.
/// </summary>
/// <remarks>
/// <para>
/// Hard links are what make side-by-side versions affordable: roughly 17.5 MB of a 21 MB payload is
/// byte-identical between builds, so linking means those bytes exist once no matter how many versions
/// are installed. Two consequences follow and are not optional: an install directory must be treated
/// as read-only by everything except the installer, since a write through one path is visible through
/// all of them; and uninstall must DELETE files, which decrements the link count, never truncate them.
/// </para>
/// <para>
/// Linking fails across volumes and on some ReFS configurations. That is expected, not exceptional,
/// so the caller falls back to copying.
/// </para>
/// <para>
/// The directory rename is the atomic swap. Measured on this machine rather than assumed: with
/// another process holding a DLL inside the directory mapped, <c>DeleteFileW</c> on that DLL fails
/// with ERROR_ACCESS_DENIED while renaming both the file and its containing directory succeed.
/// </para>
/// </remarks>
public sealed partial class WindowsFileSystemOps : IFileSystemOps
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    /// <summary>Last reason a link attempt failed, for diagnostics. 0 when none has.</summary>
    public int LastLinkError { get; private set; }

    public bool TryHardLink(string existing, string link)
    {
        if (CreateHardLinkW(link, existing, nint.Zero)) return true;

        // Any failure means "copy instead", never "fail the install": links are an optimisation, and
        // the common causes are ordinary -- 17 ERROR_NOT_SAME_DEVICE across volumes, 1 or 50 where
        // the filesystem has no link support. The code is kept for diagnosis rather than branching.
        LastLinkError = Marshal.GetLastWin32Error();
        return false;
    }

    public void MoveDirectory(string from, string to)
    {
        // WRITE_THROUGH so the rename is committed before this returns; without it a power loss
        // between the rename and the flush can leave neither name pointing at the payload.
        if (!MoveFileExW(from, to, MoveFileWriteThrough))
        {
            throw new IOException(
                $"could not move {from} to {to}",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())?.HResult ?? 0);
        }
    }

    /// <summary>Replaces a file atomically, for the launcher's own self-update.</summary>
    public static bool ReplaceFile(string source, string destination)
        => MoveFileExW(source, destination, MoveFileReplaceExisting | MoveFileWriteThrough);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);
}
