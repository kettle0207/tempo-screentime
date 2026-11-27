using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Tempo
{
    internal static class AppMetadata
    {
        // Version resources and shell icons are loaded by the dashboard, once per application.
        internal static void Register(History history, string id)
        {
            var exe = Path.GetFileNameWithoutExtension(id);
            var name = exe;
            var image = "";
            try
            {
                if (id.Length > 2 && id[1] == ':' && new DriveInfo(id.Substring(0, 1)).DriveType == DriveType.Fixed)
                {
                    var desc = FileVersionInfo.GetVersionInfo(id).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) name = desc;
                    using (var icon = Icon.ExtractAssociatedIcon(id))
                    using (var bitmap = icon?.ToBitmap())
                    using (var stream = new MemoryStream())
                    {
                        if (bitmap != null)
                        {
                            bitmap.Save(stream, ImageFormat.Png);
                            image = Convert.ToBase64String(stream.ToArray());
                        }
                    }
                }
            }
            catch (Exception) { /* Moved and protected executables retain their process name. */ }
            history.Register(id, name, image, Categories.App(exe, id));
        }
    }
}
