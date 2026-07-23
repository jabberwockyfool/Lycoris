using System;
using System.Runtime.InteropServices;

namespace Lycoris
{
    /// <summary>
    /// Modern Vista-style folder picker (COM IFileOpenDialog + FOS_PICKFOLDERS). Unlike WinForms
    /// FolderBrowserDialog (the old white tree dialog that ignores dark mode), this one is the common
    /// item dialog and follows the Windows dark theme. Returns the chosen path, or null if cancelled.
    /// </summary>
    public static class FolderPicker
    {
        public static string Pick(string title, IntPtr owner)
        {
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogRcw();
                dlg.GetOptions(out uint opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);

                int hr = dlg.Show(owner);
                if (hr != 0) return null; // S_OK == 0; anything else (incl. cancel) -> no selection

                dlg.GetResult(out IShellItem item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pszPath);
                string path = Marshal.PtrToStringAuto(pszPath);
                Marshal.FreeCoTaskMem(pszPath);
                Marshal.ReleaseComObject(item);
                return path;
            }
            catch
            {
                // Fallback to the classic dialog if the COM one is unavailable.
                using (var fb = new System.Windows.Forms.FolderBrowserDialog { Description = title })
                    return fb.ShowDialog() == System.Windows.Forms.DialogResult.OK ? fb.SelectedPath : null;
            }
            finally { if (dlg != null) Marshal.ReleaseComObject(dlg); }
        }

        private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        // Vtable order: IModalWindow, then IFileDialog, then IFileOpenDialog. Unused methods keep their
        // slots (parameters only matter for the methods we actually call).
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);                                   // IModalWindow
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);                 // IFileDialog…
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);                                     // IFileOpenDialog
            void GetSelectedItems(out IntPtr ppsai);
        }
    }
}
