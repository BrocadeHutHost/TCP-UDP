using System.Buffers;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;

namespace VideoStreamSimulationTransmission
{
    public partial class MainWindow : System.Windows.Window
    {
        #region 数据传输相关
        private Socket? tcpSocket;
        private UdpClient? udpClient;
        private TcpListener? tcpListener;
        private Socket? tcpAcceptedClient;
        private SerialPort? serialPort;
        private bool isConnected = false;
        private bool isServer = false;
        private long txCount = 0, rxCount = 0;
        private DispatcherTimer? autoSendTimer;
        private CancellationTokenSource? receiveCts;
        #endregion

        #region 视频传输相关
        private Socket? videoUdpSocket;
        private Socket? videoTcpSocket;        // TCP客户端socket或服务端accepted socket
        private TcpListener? videoTcpListener;
        private volatile bool isVideoConnected = false;
        private volatile bool isVideoReceiveRunning = false;
        private VideoCapture? videoCapture;
        private volatile bool isVideoRunning = false;
        private Thread? videoSendThread;
        private Thread? videoReceiveThread;
        private long frameCount = 0;
        private long bytesSent = 0;
        private DateTime startTime;
        private DispatcherTimer? statsTimer;
        private bool videoUseTcp = false;

        private const int MaxRecvSize = 921600;
        private const int TcpHeaderSize = 8;  // 1B类型 + 3B保留 + 4B长度
        private const byte PacketTypeVideoFrame = 0x01;
        private int currentFps = 30;
        #endregion

        private volatile bool _windowClosing = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeControls();
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _windowClosing = true;
            autoSendTimer?.Stop();
            statsTimer?.Stop();
            DisconnectDataAll();
            StopVideo();
        }

        private void SafeDispatch(Action action)
        {
            if (_windowClosing) return;
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    if (!_windowClosing) action();
                }
                else
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_windowClosing) action();
                    });
                }
            }
            catch { }
        }

        private void InitializeControls()
        {
            RefreshSerialPorts();
            sldQuality.ValueChanged += SldQuality_ValueChanged;

            statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statsTimer.Tick += StatsTimer_Tick;
            statsTimer.Start();
        }

        private void RefreshSerialPorts()
        {
            var ports = SerialPort.GetPortNames();
            cbSerialPort.ItemsSource = ports;
            if (ports.Length > 0)
                cbSerialPort.SelectedIndex = 0;
        }

        #region 数据传输 - 角色切换

        private void cbDataRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (pnlDataClient == null) return;
            if (cbDataRole.SelectedIndex == 0)
            {
                pnlDataClient.Visibility = Visibility.Visible;
                pnlDataServer.Visibility = Visibility.Collapsed;
                pnlSerial.Visibility = Visibility.Collapsed;
                btnConnect.Content = "连接";
            }
            else
            {
                pnlDataClient.Visibility = Visibility.Collapsed;
                pnlDataServer.Visibility = Visibility.Visible;
                pnlSerial.Visibility = Visibility.Collapsed;
                btnConnect.Content = "启动监听";
            }
        }

        private void cbVideoRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (pnlVideoClient == null) return;
            if (cbVideoRole.SelectedIndex == 0)
            {
                pnlVideoClient.Visibility = Visibility.Visible;
                pnlVideoServer.Visibility = Visibility.Collapsed;
                btnVideoConnect.Content = "连接服务端";
            }
            else
            {
                pnlVideoClient.Visibility = Visibility.Collapsed;
                pnlVideoServer.Visibility = Visibility.Visible;
                btnVideoConnect.Content = "启动监听";
            }
        }

        private void cbVideoProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbVideoProtocol == null) return;
            videoUseTcp = cbVideoProtocol.SelectedIndex == 1;
        }

        private void SldQuality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            txtQuality.Text = ((int)sldQuality.Value).ToString();
        }

        #endregion

        #region 数据传输功能

        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int roleIdx = cbDataRole.SelectedIndex;
                int typeIdx = cbDataType.SelectedIndex;

                if (typeIdx == 2) { ConnectSerial(); return; }

                isServer = (roleIdx == 1);

                if (isServer)
                {
                    int localPort = int.Parse(txtLocalPort.Text);
                    if (typeIdx == 0) StartDataTcpServer(localPort);
                    else StartDataUdpServer(localPort);
                }
                else
                {
                    string remoteIP = txtRemoteIP.Text;
                    int remotePort = int.Parse(txtRemotePort.Text);
                    if (typeIdx == 0) ConnectDataTcpClient(remoteIP, remotePort);
                    else ConnectDataUdpClient(remoteIP, remotePort);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartDataTcpServer(int localPort)
        {
            tcpListener = new TcpListener(IPAddress.Any, localPort);
            tcpListener.Start();
            isServer = true;
            UpdateConnectionState(true, $"TCP服务端监听中... 端口:{localPort}");

            Task.Run(() =>
            {
                try
                {
                    tcpAcceptedClient = tcpListener.AcceptSocket();
                    SafeDispatch(() =>
                    {
                        isConnected = true;
                        UpdateConnectionState(true, $"TCP服务端 - 客户端已接入");
                    });
                    StartReceiveFromAcceptedTcpClient();
                }
                catch
                {
                    SafeDispatch(() =>
                    {
                        isConnected = false;
                        UpdateConnectionState(false, "未连接");
                    });
                }
            });
        }

        private void StartReceiveFromAcceptedTcpClient()
        {
            receiveCts = new CancellationTokenSource();
            var token = receiveCts.Token;
            Task.Run(async () =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    while (!token.IsCancellationRequested && !_windowClosing && tcpAcceptedClient != null && tcpAcceptedClient.Connected)
                    {
                        int len = await tcpAcceptedClient.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None, token);
                        if (len > 0)
                        {
                            var data = new byte[len];
                            Buffer.BlockCopy(buffer, 0, data, 0, len);
                            rxCount += len;
                            SafeDispatch(() => DisplayReceivedData(data));
                        }
                    }
                }
                catch { }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }, token);
        }

        private void ConnectDataTcpClient(string remoteIP, int remotePort)
        {
            tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcpSocket.Connect(new IPEndPoint(IPAddress.Parse(remoteIP), remotePort));
            isConnected = true;
            isServer = false;
            UpdateConnectionState(true, $"TCP客户端已连接 {remoteIP}:{remotePort}");
            StartReceiveDataTcpClient();
        }

        private void StartReceiveDataTcpClient()
        {
            receiveCts = new CancellationTokenSource();
            var token = receiveCts.Token;
            Task.Run(async () =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    while (!token.IsCancellationRequested && !_windowClosing && tcpSocket != null)
                    {
                        int len = await tcpSocket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None, token);
                        if (len > 0)
                        {
                            var data = new byte[len];
                            Buffer.BlockCopy(buffer, 0, data, 0, len);
                            rxCount += len;
                            SafeDispatch(() => DisplayReceivedData(data));
                        }
                    }
                }
                catch { }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }, token);
        }

        private void StartDataUdpServer(int localPort)
        {
            udpClient = new UdpClient(localPort);
            isServer = true;
            isConnected = true;
            UpdateConnectionState(true, $"UDP服务端监听中... 端口:{localPort}");
            StartReceiveDataUdp();
        }

        private void ConnectDataUdpClient(string remoteIP, int remotePort)
        {
            udpClient = new UdpClient();
            udpClient.Connect(remoteIP, remotePort);
            isServer = false;
            isConnected = true;
            UpdateConnectionState(true, $"UDP客户端已绑定目标 {remoteIP}:{remotePort}");
            StartReceiveDataUdp();
        }

        private void StartReceiveDataUdp()
        {
            receiveCts = new CancellationTokenSource();
            var token = receiveCts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && !_windowClosing && udpClient != null)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(token);
                        if (result.Buffer.Length > 0)
                        {
                            rxCount += result.Buffer.Length;
                            SafeDispatch(() => DisplayReceivedData(result.Buffer));
                        }
                    }
                    catch { break; }
                }
            }, token);
        }

        private void ConnectSerial()
        {
            if (cbSerialPort.SelectedItem == null)
            {
                MessageBox.Show("请选择串口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string portName = cbSerialPort.SelectedItem.ToString()!;
            int baudRate = int.Parse((cbBaudRate.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "115200");
            serialPort = new SerialPort(portName, baudRate);
            serialPort.DataReceived += SerialPort_DataReceived;
            serialPort.Open();
            isConnected = true;
            isServer = false;
            UpdateConnectionState(true, $"串口已打开 {portName} {baudRate}");
        }

        private void UpdateConnectionState(bool connected, string status)
        {
            isConnected = connected;
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
            btnSend.IsEnabled = connected;
            txtStatus.Text = status;
        }

        private void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectDataAll();
            UpdateConnectionState(false, "未连接");
        }

        private void DisconnectDataAll()
        {
            receiveCts?.Cancel();
            receiveCts?.Dispose();
            receiveCts = null;

            try { tcpSocket?.Shutdown(SocketShutdown.Both); } catch { }
            tcpSocket?.Close();
            tcpSocket = null;

            try { tcpAcceptedClient?.Shutdown(SocketShutdown.Both); } catch { }
            tcpAcceptedClient?.Close();
            tcpAcceptedClient = null;

            tcpListener?.Stop();
            tcpListener = null;

            udpClient?.Close();
            udpClient = null;

            if (serialPort?.IsOpen == true)
                serialPort.Close();
            serialPort = null;

            isConnected = false;
            isServer = false;
        }

        private void btnSend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (chkAutoSend.IsChecked == true) ToggleAutoSend();
                else SendData();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleAutoSend()
        {
            if (autoSendTimer == null)
            {
                autoSendTimer = new DispatcherTimer();
                autoSendTimer.Tick += (s, args) => SendData();
                autoSendTimer.Interval = TimeSpan.FromMilliseconds(int.Parse(txtInterval.Text));
                autoSendTimer.Start();
                btnSend.Content = "停止发送";
            }
            else
            {
                autoSendTimer.Stop();
                autoSendTimer = null;
                btnSend.Content = "发送";
            }
        }

        private void SendData()
        {
            if (!isConnected || string.IsNullOrEmpty(txtSendData.Text)) return;

            byte[] data = chkHexSend.IsChecked == true
                ? HexStringToBytes(txtSendData.Text)
                : Encoding.UTF8.GetBytes(txtSendData.Text);

            try
            {
                int typeIdx = cbDataType.SelectedIndex;
                if (typeIdx == 0)
                {
                    if (isServer) tcpAcceptedClient?.Send(data);
                    else tcpSocket?.Send(data);
                }
                else if (typeIdx == 1)
                {
                    udpClient?.Send(data, data.Length);
                }
                else
                {
                    serialPort?.Write(data, 0, data.Length);
                }
                txCount += data.Length;
                UpdateStats();
            }
            catch { }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (serialPort == null) return;
                int len = serialPort.BytesToRead;
                byte[] data = new byte[len];
                serialPort.Read(data, 0, len);
                rxCount += len;
                SafeDispatch(() => DisplayReceivedData(data));
            }
            catch { }
        }

        private void DisplayReceivedData(byte[] data)
        {
            string text = chkHexDisplay.IsChecked == true
                ? BitConverter.ToString(data).Replace("-", " ")
                : Encoding.UTF8.GetString(data);

            txtReceiveData.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
            if (chkAutoScroll.IsChecked == true)
                txtReceiveData.ScrollToEnd();
            UpdateStats();
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtReceiveData.Clear();
            txCount = rxCount = 0;
            UpdateStats();
        }

        private void UpdateStats()
        {
            txtTXCount.Text = $"TX: {txCount}";
            txtRXCount.Text = $"RX: {rxCount}";
        }

        private static byte[] HexStringToBytes(string hex)
        {
            hex = hex.Replace(" ", "").Replace("\r", "").Replace("\n", "");
            if (hex.Length % 2 != 0) hex = "0" + hex;
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        #endregion

        #region 视频传输功能

        private void btnSelectVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
                txtVideoPath.Text = dlg.FileName;
        }

        private void btnVideoConnect_Click(object sender, RoutedEventArgs e)
        {
            videoUseTcp = cbVideoProtocol.SelectedIndex == 1;

            if (cbVideoRole.SelectedIndex == 1)
            {
                // 服务端
                StartVideoServer();
            }
            else
            {
                // 客户端
                StartVideoClient();
            }
        }

        private void btnVideoDisconnect_Click(object sender, RoutedEventArgs e) => StopVideo();

        // ==================== 客户端（发送端） ====================

        private void StartVideoClient()
        {
            string remoteIP = txtVideoRemoteIP.Text;
            int remotePort = int.Parse(txtVideoRemotePort.Text);

            if (videoUseTcp)
            {
                // TCP客户端：先连接，成功后才标记已连接
                try
                {
                    videoTcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    videoTcpSocket.NoDelay = true;
                    videoTcpSocket.SendBufferSize = 1024 * 1024;
                    videoTcpSocket.ReceiveBufferSize = 1024 * 1024;
                    videoTcpSocket.Connect(new IPEndPoint(IPAddress.Parse(remoteIP), remotePort));

                    isVideoConnected = true;
                    isVideoReceiveRunning = true;

                    // 启动TCP接收线程（双向视频支持）
                    videoReceiveThread = new Thread(() => ReceiveVideoTcpLoop(videoTcpSocket))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    videoReceiveThread.Start();

                    UpdateVideoConnectionState(true, $"TCP已连接 {remoteIP}:{remotePort}");
                }
                catch (Exception ex)
                {
                    videoTcpSocket?.Close();
                    videoTcpSocket = null;
                    MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                // UDP客户端
                videoUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                videoUdpSocket.SendBufferSize = 1024 * 1024;

                isVideoConnected = true;
                isVideoReceiveRunning = true;

                // UDP接收线程
                videoReceiveThread = new Thread(() => ReceiveVideoUdpLoop(videoUdpSocket))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };
                videoReceiveThread.Start();

                UpdateVideoConnectionState(true, $"UDP已就绪 - 目标 {remoteIP}:{remotePort}");
            }
        }

        // ==================== 服务端（接收端） ====================

        private void StartVideoServer()
        {
            int localPort = int.Parse(txtVideoLocalPort.Text);

            if (videoUseTcp)
            {
                // TCP服务端：启动监听，循环等待客户端连接
                try
                {
                    videoTcpListener = new TcpListener(IPAddress.Any, localPort);
                    videoTcpListener.Start();

                    isVideoConnected = true;
                    UpdateVideoConnectionState(true, $"TCP监听中... 端口:{localPort}，等待客户端");

                    // 后台线程循环等待客户端连接
                    Task.Run(() => TcpServerAcceptLoop(localPort));
                }
                catch (Exception ex)
                {
                    videoTcpListener?.Stop();
                    videoTcpListener = null;
                    MessageBox.Show($"启动监听失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                // UDP服务端：绑定端口并启动接收
                try
                {
                    videoUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    videoUdpSocket.ReceiveBufferSize = 2 * 1024 * 1024;
                    videoUdpSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));

                    isVideoConnected = true;
                    isVideoReceiveRunning = true;

                    UpdateVideoConnectionState(true, $"UDP监听中... 端口:{localPort}");

                    // 启动UDP接收线程
                    videoReceiveThread = new Thread(() => ReceiveVideoUdpLoop(videoUdpSocket))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    videoReceiveThread.Start();
                }
                catch (Exception ex)
                {
                    videoUdpSocket?.Close();
                    videoUdpSocket = null;
                    MessageBox.Show($"绑定端口失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// TCP服务端接受连接循环 - 客户端断开后继续等待新连接
        /// </summary>
        private void TcpServerAcceptLoop(int localPort)
        {
            while (isVideoConnected && !_windowClosing && videoTcpListener != null)
            {
                try
                {
                    SafeDispatch(() => UpdateVideoConnectionState(true, $"TCP监听中... 端口:{localPort}，等待客户端"));

                    Socket clientSock = videoTcpListener.AcceptSocket();
                    clientSock.NoDelay = true;
                    clientSock.SendBufferSize = 1024 * 1024;
                    clientSock.ReceiveBufferSize = 2 * 1024 * 1024;
                    videoTcpSocket = clientSock;

                    SafeDispatch(() => UpdateVideoConnectionState(true, $"TCP客户端已接入，端口:{localPort}"));

                    // 启动TCP接收线程，等待其结束
                    isVideoReceiveRunning = true;
                    var receiveThread = new Thread(() => ReceiveVideoTcpLoop(clientSock))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    receiveThread.Start();
                    receiveThread.Join(); // 等待接收线程结束（客户端断开）

                    // 客户端断开后，关闭该连接，继续等待新连接
                    try { clientSock.Shutdown(SocketShutdown.Both); } catch { }
                    clientSock.Close();
                    videoTcpSocket = null;

                    SafeDispatch(() => UpdateVideoConnectionState(true, $"TCP客户端已断开，等待新连接..."));
                }
                catch (ObjectDisposedException)
                {
                    break; // 服务端停止
                }
                catch (SocketException)
                {
                    // Accept被中断，检查是否需要继续
                    if (!isVideoConnected || _windowClosing) break;
                }
                catch (Exception ex)
                {
                    SafeDispatch(() => UpdateVideoConnectionState(false, $"TCP监听异常: {ex.Message}"));
                    break;
                }
            }
        }

        // ==================== UDP接收 ====================

        private void ReceiveVideoUdpLoop(Socket sock)
        {
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] buffer = new byte[MaxRecvSize];

            while (isVideoReceiveRunning && !_windowClosing)
            {
                try
                {
                    if (sock.Available == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int len = sock.ReceiveFrom(buffer, ref remoteEP);
                    if (len <= 0) continue;

                    byte[] jpegData = new byte[len];
                    Buffer.BlockCopy(buffer, 0, jpegData, 0, len);

                    frameCount++;
                    SafeDispatch(() => DisplayVideoFrame(jpegData));
                }
                catch (SocketException)
                {
                    if (isVideoReceiveRunning && !_windowClosing)
                        Thread.Sleep(10);
                    else
                        break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }

        // ==================== TCP接收 ====================

        private void ReceiveVideoTcpLoop(Socket sock)
        {
            byte[] headerBuf = new byte[TcpHeaderSize];
            int headerOffset = 0;
            int expectedFrameLen = 0;
            byte[]? frameBuf = null;
            int frameOffset = 0;

            while (isVideoReceiveRunning && !_windowClosing)
            {
                try
                {
                    // 阶段1: 读取帧长度头
                    if (expectedFrameLen == 0)
                    {
                        int headerNeeded = TcpHeaderSize - headerOffset;
                        int read = sock.Receive(headerBuf, headerOffset, headerNeeded, SocketFlags.None);
                        if (read <= 0) break;
                        headerOffset += read;

                        if (headerOffset >= TcpHeaderSize)
                        {
                            byte packetType = headerBuf[0];
                            if (packetType != PacketTypeVideoFrame)
                            {
                                // 未知类型，丢弃
                                expectedFrameLen = 0;
                                headerOffset = 0;
                                continue;
                            }
                            expectedFrameLen = (headerBuf[4] << 24) | (headerBuf[5] << 16) | (headerBuf[6] << 8) | headerBuf[7];

                            if (expectedFrameLen <= 0 || expectedFrameLen > MaxRecvSize)
                            {
                                expectedFrameLen = 0;
                                headerOffset = 0;
                                continue;
                            }

                            frameBuf = new byte[expectedFrameLen];
                            frameOffset = 0;
                            headerOffset = 0;
                        }
                    }
                    // 阶段2: 读取帧数据
                    else
                    {
                        int needed = expectedFrameLen - frameOffset;
                        int read = sock.Receive(frameBuf, frameOffset, needed, SocketFlags.None);
                        if (read <= 0) break;
                        frameOffset += read;

                        if (frameOffset >= expectedFrameLen)
                        {
                            frameCount++;
                            byte[] jpegData = frameBuf!;
                            SafeDispatch(() => DisplayVideoFrame(jpegData));

                            expectedFrameLen = 0;
                            frameBuf = null;
                            frameOffset = 0;
                        }
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }

        // ==================== 显示帧 ====================

        private void DisplayVideoFrame(byte[] jpegData)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(jpegData))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                imgVideo.Source = bitmap;
                txtVideoInfo.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // ==================== 发送控制 ====================

        private void btnStartVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!isVideoConnected)
            {
                MessageBox.Show("请先连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (rbVideoFile.IsChecked == true)
            {
                if (string.IsNullOrEmpty(txtVideoPath.Text) || txtVideoPath.Text == "未选择")
                {
                    MessageBox.Show("请选择视频文件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                StartVideoFileSend();
            }
            else if (rbCamera.IsChecked == true)
            {
                StartCameraSend();
            }
        }

        private void btnStopVideo_Click(object sender, RoutedEventArgs e) => StopVideoTransmission();

        private void StartVideoFileSend()
        {
            videoCapture = new VideoCapture(txtVideoPath.Text);
            if (!videoCapture.IsOpened())
            {
                MessageBox.Show("无法打开视频文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            BeginVideoSending(isFile: true);
        }

        private void StartCameraSend()
        {
            int cameraIndex = cbCamera.SelectedIndex > 0 ? cbCamera.SelectedIndex - 1 : 0;
            videoCapture = new VideoCapture(cameraIndex);
            if (!videoCapture.IsOpened())
            {
                MessageBox.Show("无法打开摄像头", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            BeginVideoSending(isFile: false);
        }

        private void BeginVideoSending(bool isFile)
        {
            int fps = int.Parse((cbFPS.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "30");
            int quality = (int)sldQuality.Value;
            string remoteIP = txtVideoRemoteIP.Text;
            int remotePort = int.Parse(txtVideoRemotePort.Text);

            currentFps = fps;
            isVideoRunning = true;
            startTime = DateTime.Now;
            frameCount = 0;
            bytesSent = 0;

            btnStartVideo.IsEnabled = false;
            btnStopVideo.IsEnabled = true;

            bool isFileCopy = isFile;
            bool useTcp = videoUseTcp;
            Socket? tcpSock = videoTcpSocket;
            Socket? udpSock = videoUdpSocket;

            videoSendThread = new Thread(() => VideoSendLoop(isFileCopy, fps, quality, remoteIP, remotePort, useTcp, tcpSock, udpSock))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            videoSendThread.Start();
        }

        private void VideoSendLoop(bool isFile, int fps, int quality, string remoteIP, int remotePort, bool useTcp, Socket? tcpSock, Socket? udpSock)
        {
            int frameInterval = 1000 / fps;
            var frame = new Mat();
            var param = new int[] { (int)ImwriteFlags.JpegQuality, quality };
            EndPoint udpRemoteEP = new IPEndPoint(IPAddress.Parse(remoteIP), remotePort);
            byte[] tcpHeader = new byte[TcpHeaderSize];

            var lastTime = DateTime.Now;

            while (isVideoRunning && !_windowClosing && videoCapture != null)
            {
                try
                {
                    var elapsed = (DateTime.Now - lastTime).TotalMilliseconds;
                    if (elapsed < frameInterval)
                        Thread.Sleep(Math.Max(1, (int)(frameInterval - elapsed)));
                    lastTime = DateTime.Now;

                    if (!videoCapture.Read(frame) || frame.Empty())
                    {
                        if (isFile)
                        {
                            videoCapture.Set(VideoCaptureProperties.PosFrames, 0);
                            continue;
                        }
                        break;
                    }

                    if (!Cv2.ImEncode(".jpg", frame, out var jpegBytes, param))
                        continue;
                    if (jpegBytes.Length == 0) continue;

                    bool sendOk = false;

                    if (useTcp && tcpSock != null)
                    {
                        try
                        {
                            tcpHeader[0] = PacketTypeVideoFrame;  // 类型: 视频帧
                            tcpHeader[1] = 0;                      // 保留
                            tcpHeader[2] = 0;                      // 保留
                            tcpHeader[3] = 0;                      // 保留
                            tcpHeader[4] = (byte)(jpegBytes.Length >> 24);
                            tcpHeader[5] = (byte)(jpegBytes.Length >> 16);
                            tcpHeader[6] = (byte)(jpegBytes.Length >> 8);
                            tcpHeader[7] = (byte)(jpegBytes.Length);

                            tcpSock.Send(tcpHeader);
                            tcpSock.Send(jpegBytes);
                            sendOk = true;
                        }
                        catch { break; }
                    }
                    else if (!useTcp && udpSock != null)
                    {
                        try
                        {
                            udpSock.SendTo(jpegBytes, udpRemoteEP);
                            sendOk = true;
                        }
                        catch { }
                    }

                    if (sendOk)
                    {
                        bytesSent += jpegBytes.Length;
                        frameCount++;
                    }

                    // 发送时也显示预览
                    SafeDispatch(() => DisplayVideoFrame(jpegBytes));
                }
                catch
                {
                    break;
                }
            }

            frame.Dispose();
        }

        // ==================== 停止 ====================

        /// <summary>
        /// 断开连接：停止传输 + 关闭socket
        /// </summary>
        private void StopVideo()
        {
            StopVideoTransmission();
            CloseVideoSockets();
            isVideoConnected = false;
            UpdateVideoConnectionState(false, "未连接");
        }

        /// <summary>
        /// 停止传输：只停止发送/接收线程，不断开网络
        /// </summary>
        private void StopVideoTransmission()
        {
            isVideoRunning = false;
            isVideoReceiveRunning = false;

            try { videoSendThread?.Join(2000); } catch { }
            try { videoReceiveThread?.Join(2000); } catch { }
            videoSendThread = null;
            videoReceiveThread = null;

            videoCapture?.Release();
            videoCapture?.Dispose();
            videoCapture = null;

            // 只关闭TCP客户端socket，不关闭服务端监听
            // UDP socket保持不变
            if (videoTcpSocket != null && videoTcpListener == null)
            {
                // 客户端模式，关闭连接
                try { videoTcpSocket.Shutdown(SocketShutdown.Both); } catch { }
                videoTcpSocket.Close();
                videoTcpSocket = null;
                isVideoConnected = false;
                UpdateVideoConnectionState(false, "未连接");
            }

            SafeDispatch(() =>
            {
                btnStartVideo.IsEnabled = true;
                btnStopVideo.IsEnabled = false;
            });
        }

        /// <summary>
        /// 关闭所有socket
        /// </summary>
        private void CloseVideoSockets()
        {
            try { videoTcpSocket?.Shutdown(SocketShutdown.Both); } catch { }
            videoTcpSocket?.Close();
            videoTcpSocket = null;

            videoTcpListener?.Stop();
            videoTcpListener = null;

            videoUdpSocket?.Close();
            videoUdpSocket = null;
        }

        private void UpdateVideoConnectionState(bool connected, string status)
        {
            isVideoConnected = connected;
            btnVideoConnect.IsEnabled = !connected;
            btnVideoDisconnect.IsEnabled = connected;
            txtVideoStatus.Text = $"状态: {status}";
        }

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            if (isVideoRunning && startTime != DateTime.MinValue)
            {
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                if (elapsed > 0)
                {
                    var bitrate = bytesSent / elapsed / 1024;
                    txtBitrate.Text = $"码率: {bitrate:F1} KB/s";
                }
                txtFrameCount.Text = $"帧数: {frameCount}";
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}
