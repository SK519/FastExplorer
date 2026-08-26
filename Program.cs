using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastExplorer
{
    public static class Program
    {
        private const string MutexName = "FastExplorer_SingleInstance_Mutex_Global";
        private const string PipeName = "FastExplorer_SingleInstance_Pipe_Global";

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
        private const int ASFW_ANY = -1;

        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogLaunch(string message)
        {
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, "launch.log");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            string[] fullCmdArgs = Environment.GetCommandLineArgs();
            LogLaunch($"[Main] Started. Args: {string.Join(" | ", fullCmdArgs)}");

            using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);

            if (!isFirstInstance)
            {
                // 既に起動中のプロセスが存在する場合: Named Pipe で引数を送信して即座に終了
                try
                {
                    AllowSetForegroundWindow(ASFW_ANY);
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(500);
                    using var writer = new StreamWriter(client, Encoding.UTF8);
                    string payload = string.Join("\n", fullCmdArgs);
                    writer.WriteLine(payload);
                    writer.Flush();
                    LogLaunch($"[Main] Sent payload to existing instance and exiting.");
                }
                catch (Exception ex)
                {
                    LogLaunch($"[Main] Failed to send pipe message: {ex.Message}");
                }
                return;
            }

            // メインインスタンス: Named Pipe サーバーをバックグラウンドでリッスン
            StartPipeServer();

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }

        private static void StartPipeServer()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync();

                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var receivedLines = new System.Collections.Generic.List<string>();
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            receivedLines.Add(line);
                        }

                        if (receivedLines.Count > 0)
                        {
                            App.HandleRemoteActivation(receivedLines.ToArray());
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Program] PipeServer error: {ex.Message}");
                        await Task.Delay(500);
                    }
                }
            });
        }
    }
}
