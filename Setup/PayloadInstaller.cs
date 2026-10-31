using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace WinTV.Setup;

internal static class PayloadInstaller
{
    internal static long Apply(ZipArchive archive, string targetDir, Action<int>? progress)
    {
        var root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var work = Path.Combine(Path.GetTempPath(), "WinTV_Install_" + Guid.NewGuid().ToString("N"));
        var files = new List<(string Source, string Destination, string Backup)>();
        var changed = new List<(string Destination, string? Backup)>();
        long bytes = 0;
        bool keepBackup = false;
        Directory.CreateDirectory(work);
        try
        {
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (relative.StartsWith("WinTV\\", StringComparison.OrdinalIgnoreCase)) relative = relative[6..];
                if (relative.Length == 0 || entry.Name.Length == 0) continue;
                var dest = Path.GetFullPath(Path.Combine(root, relative));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Файл архива выходит за папку установки.");
                var source = Path.Combine(work, "stage", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                entry.ExtractToFile(source);
                files.Add((source, dest, Path.Combine(work, "backup", relative)));
                bytes += entry.Length;
            }
            if (!files.Exists(f => string.Equals(f.Destination, Path.Combine(root, "WinTV.exe"), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("В архиве отсутствует WinTV.exe.");
            foreach (var file in files)
            {
                string? backup = null;
                if (File.Exists(file.Destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Backup)!);
                    File.Copy(file.Destination, file.Backup);
                    backup = file.Backup;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                changed.Add((file.Destination, backup));
                File.Copy(file.Source, file.Destination, true);
                progress?.Invoke(changed.Count * 100 / files.Count);
            }
            return bytes;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            for (int i = changed.Count - 1; i >= 0; i--)
            {
                try
                {
                    var file = changed[i];
                    if (file.Backup != null) File.Copy(file.Backup, file.Destination, true);
                    else if (File.Exists(file.Destination)) File.Delete(file.Destination);
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 1)
            {
                keepBackup = true;
                throw new AggregateException("Не удалось полностью восстановить файлы. Резервная копия: " + work, errors);
            }
            throw;
        }
        finally
        {
            if (!keepBackup)
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
