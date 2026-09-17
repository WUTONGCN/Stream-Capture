using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamCapture.Core
{
    /// <summary>
    /// 命名管道 IPC 服务器 - 接收来自注入 DLL 的数据
    /// </summary>
    public class NamedPipeIpcServer : IDisposable
    {
        // 固定的管道名称，必须与 Hook DLL 中的名称一致
        private const string PIPE_NAME = "KuaishouHookPipe";

        private readonly Action<string> _logger;
        private readonly Action<byte[], bool> _onDataCaptured;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;

        public NamedPipeIpcServer(Action<string> logger, Action<byte[], bool> onDataCaptured)
        {
            _logger = logger;
            _onDataCaptured = onDataCaptured;
        }

        public string PipeName => PIPE_NAME;

        /// <summary>
        /// 启动 IPC 服务器
        /// </summary>
        public void Start()
        {
            if (_serverTask != null)
            {
                _logger("⚠️  IPC 服务器已在运行");
                return;
            }

            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(() => ServerLoop(_cts.Token));
            _logger($"✅ IPC 服务器已启动: {PIPE_NAME}");
        }

        /// <summary>
        /// 停止 IPC 服务器
        /// </summary>
        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _serverTask?.Wait(TimeSpan.FromSeconds(5));
                _cts?.Dispose();
                _cts = null;
                _serverTask = null;
                _logger("✅ IPC 服务器已停止");
            }
            catch (Exception ex)
            {
                _logger($"⚠️  停止 IPC 服务器异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 服务器循环 - 持续监听客户端连接
        /// </summary>
        private async Task ServerLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var pipeServer = new NamedPipeServerStream(
                        PIPE_NAME,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    _logger("💤 等待客户端连接...");

                    // 等待客户端连接
                    await pipeServer.WaitForConnectionAsync(cancellationToken);
                    _logger("✅ 客户端已连接");

                    // 处理客户端请求
                    await HandleClient(pipeServer, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                    break;
                }
                catch (Exception ex)
                {
                    _logger($"⚠️  IPC 服务器异常: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// 处理客户端请求
        /// </summary>
        private async Task HandleClient(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
        {
            try
            {
                using var reader = new BinaryReader(pipeServer, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(pipeServer, Encoding.UTF8, leaveOpen: true);

                // 发送握手消息
                writer.Write("HELLO");
                writer.Flush();

                while (pipeServer.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // 读取消息类型
                        var messageType = reader.ReadByte();

                        switch (messageType)
                        {
                            case 1: // 日志消息
                                var logMessage = reader.ReadString();
                                _logger($"[Hook] {logMessage}");
                                break;

                            case 2: // 数据捕获（发送）
                                var sendDataLength = reader.ReadInt32();
                                var sendData = reader.ReadBytes(sendDataLength);
                                _onDataCaptured?.Invoke(sendData, true);
                                break;

                            case 3: // 数据捕获（接收）
                                var recvDataLength = reader.ReadInt32();
                                var recvData = reader.ReadBytes(recvDataLength);
                                _onDataCaptured?.Invoke(recvData, false);
                                break;

                            case 4: // 心跳
                                writer.Write((byte)4); // 响应心跳
                                writer.Flush();
                                break;

                            default:
                                _logger($"⚠️  未知消息类型: {messageType}");
                                break;
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        // 客户端断开连接
                        break;
                    }
                }

                _logger("✅ 客户端已断开");
            }
            catch (Exception ex)
            {
                _logger($"⚠️  处理客户端异常: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
