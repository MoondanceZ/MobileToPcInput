using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace pc_receiver;

public enum VbCableInstallOutcome
{
    Completed,
    Canceled
}

public static class VbCableInstallerService
{
    private const string ResourceName =
        "MobileToPcInput.Assets.VBCABLE_Driver_Pack45.zip";
    private const string SetupFileName = "VBCABLE_Setup_x64.exe";

    public static async Task<VbCableInstallOutcome> InstallAsync(
        CancellationToken cancellationToken = default)
    {
        var extractionDirectory = Path.Combine(
            Path.GetTempPath(),
            "MobileToPcInput",
            $"vb-cable-{Guid.NewGuid():N}");

        Directory.CreateDirectory(extractionDirectory);
        try
        {
            await ExtractPackageAsync(extractionDirectory, cancellationToken);
            var setupPath = Path.Combine(extractionDirectory, SetupFileName);
            if (!File.Exists(setupPath))
            {
                throw new FileNotFoundException(
                    $"内嵌的 VB-CABLE 安装包缺少 {SetupFileName}。",
                    setupPath);
            }

            Process? process;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    WorkingDirectory = extractionDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return VbCableInstallOutcome.Canceled;
            }

            if (process is null)
            {
                throw new InvalidOperationException("无法启动 VB-CABLE 安装程序。");
            }

            using (process)
            {
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"VB-CABLE 安装程序退出，代码：{process.ExitCode}。");
                }
            }

            return VbCableInstallOutcome.Completed;
        }
        finally
        {
            TryDeleteExtractionDirectory(extractionDirectory);
        }
    }

    private static async Task ExtractPackageAsync(
        string extractionDirectory,
        CancellationToken cancellationToken)
    {
        await using var packageStream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("程序中没有找到内嵌的 VB-CABLE 安装包。");
        await using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        var extractionRoot = Path.GetFullPath(extractionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = Path.GetFullPath(
                Path.Combine(extractionDirectory, entry.FullName));
            if (!destinationPath.StartsWith(
                    extractionRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"VB-CABLE 安装包包含不安全的路径：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var entryStream = await entry.OpenAsync(cancellationToken);
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await entryStream.CopyToAsync(destinationStream, cancellationToken);
        }
    }

    private static void TryDeleteExtractionDirectory(string extractionDirectory)
    {
        try
        {
            if (Directory.Exists(extractionDirectory))
            {
                Directory.Delete(extractionDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                $"VB-CABLE temporary directory cleanup failed: {extractionDirectory}",
                ex);
        }
    }
}
