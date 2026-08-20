using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace UvexAdv.Reduction.Launcher;

internal static class Program
{
    private const string SettingsFileName = "launcher.settings.json";

    [STAThread]
    private static void Main()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
            if (!File.Exists(settingsPath))
            {
                throw new FileNotFoundException("启动器配置不存在。请重新运行快捷方式安装程序。", settingsPath);
            }

            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(settingsPath));
            if (settings is null || string.IsNullOrWhiteSpace(settings.ProjectRoot))
            {
                throw new InvalidDataException("启动器配置中缺少 ProjectRoot。");
            }

            var projectRoot = Path.GetFullPath(settings.ProjectRoot);
            var reductionRoot = Path.Combine(projectRoot, "reduction");
            var pythonw = Path.Combine(reductionRoot, ".venv", "Scripts", "pythonw.exe");
            if (!File.Exists(pythonw))
            {
                throw new FileNotFoundException(
                    "找不到后期处理 Python 运行环境。请先安装 reduction\\requirements-lock.txt。",
                    pythonw);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonw,
                WorkingDirectory = reductionRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("uvex_reduce.gui");

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("Python 图形界面进程未能启动。");
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"UVEX-ADV 光谱处理无法启动。\n\n{error.Message}",
                "UVEX-ADV 光谱处理",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private sealed record LauncherSettings(string ProjectRoot);
}
