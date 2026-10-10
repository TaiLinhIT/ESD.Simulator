using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ESD.Protocol;

namespace ESD.Simulator.Wpf;

public partial class MainWindow : Window
{
    SerialPort?  _port;
    uint         _seq      = 1;
    int          _sent, _ackOk;
    Timer?       _autoTimer;

    // Frame parser state
    readonly List<byte> _rxBuf = new();

    public MainWindow()
    {
        InitializeComponent();
        CbBaud.ItemsSource  = new[] { 9600, 19200, 38400, 57600, 115200 };
        CbBaud.SelectedItem = 115200;
        RefreshPorts();
    }

    // ── Serial ────────────────────────────────────────────────────────────────

    void RefreshPorts()
    {
        // Lọc bỏ Bluetooth COM ports (BthModem) — chỉ giữ USB-Serial thật
        var allPorts  = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
        var btPorts   = GetBluetoothComPorts();
        var usbPorts  = allPorts.Except(btPorts).ToArray();

        // Nếu không lọc được thì hiện tất cả
        var display = usbPorts.Length > 0 ? usbPorts : allPorts;

        CbPort.ItemsSource = display;
        if (display.Length > 0) CbPort.SelectedIndex = 0;

        Log("SYS", $"Ports: [{string.Join(", ", allPorts)}]  |  Bluetooth bị ẩn: [{string.Join(", ", btPorts)}]");
    }

    /// <summary>Đọc registry tìm các COM port là Bluetooth (BthModem).</summary>
    static HashSet<string> GetBluetoothComPorts()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key == null) return result;
            foreach (var name in key.GetValueNames())
            {
                if (name.Contains("BthModem", StringComparison.OrdinalIgnoreCase))
                    result.Add(key.GetValue(name)?.ToString() ?? "");
            }
        }
        catch { }
        return result;
    }

    void OnRefresh(object? o, RoutedEventArgs e) => RefreshPorts();

    void OnConnect(object? o, RoutedEventArgs e)
    {
        try
        {
            if (CbPort.SelectedItem is not string port) { Log("ERROR", "Chọn COM port"); return; }

            // Đóng port cũ nếu còn sót
            ClosePort();

            _port = new SerialPort(port, (int)CbBaud.SelectedItem!, Parity.None, 8, StopBits.One)
            {
                // Không đặt ReadTimeout/WriteTimeout — tránh semaphore timeout trên USB-Serial
                ReadTimeout  = SerialPort.InfiniteTimeout,
                WriteTimeout = SerialPort.InfiniteTimeout,
                // Tắt handshaking hoàn toàn — quan trọng với CH340/CP210x
                Handshake    = Handshake.None,
                DtrEnable    = false,
                RtsEnable    = false,
                DiscardNull  = false,
            };
            _port.DataReceived += OnData;
            _port.ErrorReceived += OnPortError;
            _port.Open();

            TxtConn.Text = $"Connected {port} @ {(int)CbBaud.SelectedItem!}";
            Log("SYS", $"Đã kết nối {port}");
        }
        catch (UnauthorizedAccessException)
        {
            Log("ERROR", $"Port {(CbPort.SelectedItem ?? "?")} đang bị chiếm — đóng Arduino IDE / ESD Gateway trước");
        }
        catch (Exception x)
        {
            Log("ERROR", $"[{x.GetType().Name}] {x.Message}");
            Log("HINT",  "Kiểm tra đúng COM port chưa? Arduino Uno thường là COM6+, không phải COM3/COM4 (Bluetooth)");
        }
    }

    void OnPortError(object? s, SerialErrorReceivedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => Log("PORT", $"Serial error: {e.EventType}"));
    }

    void ClosePort()
    {
        if (_port == null) return;
        try
        {
            _port.DataReceived  -= OnData;
            _port.ErrorReceived -= OnPortError;
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
        }
        catch { }
        finally { _port = null; }
    }

    void OnDisconnect(object? o, RoutedEventArgs e)
    {
        _autoTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        ClosePort();
        TxtConn.Text = "Disconnected";
        Log("SYS", "Đã ngắt kết nối");
    }

    void OnData(object? s, SerialDataReceivedEventArgs e)
    {
        try
        {
            if (_port == null || !_port.IsOpen) return;
            int count = _port.BytesToRead;
            if (count <= 0) return;
            var buf = new byte[count];
            int read = _port.Read(buf, 0, count);
            if (read > 0)
                // BeginInvoke (non-blocking) tránh deadlock với UI thread
                Dispatcher.BeginInvoke(() => ParseIncoming(buf[..read]));
        }
        catch (InvalidOperationException) { /* port bị đóng */ }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => Log("ERROR", "RX: " + ex.Message));
        }
    }

    // ── Frame parser (nhận Ack/Nack từ Arduino) ───────────────────────────────

    void ParseIncoming(byte[] data)
    {
        _rxBuf.AddRange(data);
        while (true)
        {
            // Tìm SOF
            int sof = -1;
            for (int i = 0; i < _rxBuf.Count - 1; i++)
                if (_rxBuf[i] == 0xAA && _rxBuf[i + 1] == 0x55) { sof = i; break; }
            if (sof < 0) { _rxBuf.Clear(); return; }
            if (sof > 0) _rxBuf.RemoveRange(0, sof);
            if (_rxBuf.Count < 20) return;

            int payLen = (_rxBuf[18] << 8) | _rxBuf[19];
            if (payLen > 256) { _rxBuf.RemoveAt(0); continue; }
            int total = 38 + payLen;
            if (_rxBuf.Count < total) return;
            if (_rxBuf[total - 2] != 0x0D || _rxBuf[total - 1] != 0x0A)
                { _rxBuf.RemoveAt(0); continue; }

            var frame = _rxBuf.Take(total).ToArray();
            _rxBuf.RemoveRange(0, total);
            HandleResponse(frame);
        }
    }

    void HandleResponse(byte[] raw)
    {
        Log("RX", FrameCodec.ToHex(raw));

        if (!FrameCodec.TryDecode(raw, HmacKey(), out var f, out var err) || f == null)
        {
            Log("BAD", err.ToString());
            return;
        }

        TxtLast.Text = f.Command.ToString();

        switch (f.Command)
        {
            case Command.Ack:
                _ackOk++;
                TxtAck.Text = _ackOk.ToString();
                Log("ACK", $"OK — DEV={f.DeviceId} SEQ={f.Sequence}");
                break;

            case Command.Nack:
                var errCode = f.Payload.Length > 0 ? ((ErrorCode)f.Payload[0]).ToString() : "?";
                Log("NACK", $"Lý do: {errCode} — SEQ={f.Sequence}");
                break;

            case Command.StatusReport:
                ParseStatusReport(f);
                break;

            case Command.GetInfo:
                Log("INFO", Encoding.UTF8.GetString(f.Payload));
                break;

            case Command.LogUpload:
                Log("LOG", Encoding.UTF8.GetString(f.Payload));
                break;

            default:
                Log("RESP", $"CMD={f.Command} DATA={FrameCodec.ToHex(f.Payload)}");
                break;
        }
    }

    void ParseStatusReport(EsdFrame f)
    {
        if (f.Payload.Length < 3) { Log("STATUS", "(payload quá ngắn)"); return; }
        bool locked = f.Payload[0] == 1;
        bool alarm  = f.Payload[1] == 1;
        int  wlen   = f.Payload[2];
        var  worker = wlen > 0 ? Encoding.UTF8.GetString(f.Payload, 3, Math.Min(wlen, f.Payload.Length - 3)) : "(none)";
        Log("STATUS", $"Locked={locked} Alarm={alarm} Worker={worker}");
    }

    // ── Send helper ───────────────────────────────────────────────────────────

    byte[] HmacKey() => FrameCodec.FromHex(TxtKey.Text);

    void Send(Command cmd, byte[] payload)
    {
        if (_port == null || !_port.IsOpen) { Log("ERROR", "Chưa kết nối serial"); return; }
        try
        {
            uint id = uint.TryParse(TxtDeviceId.Text, out var x) ? x : 1;
            var frame = new EsdFrame(1, FrameFlags.AckRequested, id, cmd, _seq++,
                (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), payload, Array.Empty<byte>());
            var raw = FrameCodec.Encode(frame, HmacKey());

            // Write trên Task thread — không block UI, tránh semaphore timeout
            Task.Run(() =>
            {
                try { _port.Write(raw, 0, raw.Length); }
                catch (Exception ex) { Dispatcher.BeginInvoke(() => Log("ERROR", "TX: " + ex.Message)); }
            });

            _sent++;
            TxtSent.Text = _sent.ToString();
            Log("TX", $"CMD={cmd,-16} | {FrameCodec.ToHex(raw)}");
        }
        catch (Exception ex) { Log("ERROR", ex.Message); }
    }

    // ── Auto scenario ─────────────────────────────────────────────────────────

    int _autoStep = 0;
    readonly Command[] _scenario =
    {
        Command.Heartbeat,
        Command.WorkerScan,
        Command.Attach,
        Command.EsdCheck,
        Command.StatusReport,
        Command.Detach,
    };

    void OnAutoStart(object? o, RoutedEventArgs e)
    {
        int ms = int.TryParse(TxtInterval.Text, out var v) ? v : 2000;
        _autoStep = 0;
        _autoTimer?.Dispose();
        _autoTimer = new Timer(_ => Dispatcher.Invoke(RunAutoStep), null, 0, ms);
        Log("AUTO", $"Bắt đầu auto scenario, interval={ms}ms");
    }

    void OnAutoStop(object? o, RoutedEventArgs e)
    {
        _autoTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        Log("AUTO", "Đã dừng");
    }

    void RunAutoStep()
    {
        var cmd = _scenario[_autoStep % _scenario.Length];
        var payload = cmd switch
        {
            Command.WorkerScan or Command.Attach or Command.Detach
                => Encoding.UTF8.GetBytes(TxtWorkerId.Text),
            _ => Array.Empty<byte>()
        };
        Send(cmd, payload);
        _autoStep++;
    }

    // ── Manual command handlers ───────────────────────────────────────────────

    void OnHeartbeat(object? o, RoutedEventArgs e)    => Send(Command.Heartbeat,    []);
    void OnStatusReport(object? o, RoutedEventArgs e) => Send(Command.StatusReport, []);
    void OnWorkerScan(object? o, RoutedEventArgs e)   => Send(Command.WorkerScan,   Encoding.UTF8.GetBytes(TxtWorkerId.Text));
    void OnAttach(object? o, RoutedEventArgs e)       => Send(Command.Attach,       Encoding.UTF8.GetBytes(TxtWorkerId.Text));
    void OnDetach(object? o, RoutedEventArgs e)       => Send(Command.Detach,       Encoding.UTF8.GetBytes(TxtWorkerId.Text));
    void OnEsdCheck(object? o, RoutedEventArgs e)     => Send(Command.EsdCheck,     []);
    void OnGetInfo(object? o, RoutedEventArgs e)      => Send(Command.GetInfo,      []);
    void OnSetConfig(object? o, RoutedEventArgs e)    => Send(Command.SetConfig,    Encoding.UTF8.GetBytes("{\"interval\":5}"));
    void OnAlarmClear(object? o, RoutedEventArgs e)   => Send(Command.AlarmClear,   []);
    void OnLockStation(object? o, RoutedEventArgs e)  => Send(Command.LockStation,  []);
    void OnUnlockStation(object? o, RoutedEventArgs e)=> Send(Command.UnlockStation,[]);
    void OnLogUpload(object? o, RoutedEventArgs e)    => Send(Command.LogUpload,    []);
    void OnReboot(object? o, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reboot thiết bị?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Send(Command.Reboot, []);
    }

    void OnAlarmTrigger(object? o, RoutedEventArgs e)
    {
        byte reason = CbAlarmReason.SelectedItem is ComboBoxItem item
                   && byte.TryParse(item.Tag?.ToString(), out var r) ? r : (byte)0xFF;
        Send(Command.AlarmTrigger, [reason]);
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    void Log(string tag, string msg)
    {
        LstLog.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} [{tag,-7}] {msg}");
        while (LstLog.Items.Count > 500) LstLog.Items.RemoveAt(LstLog.Items.Count - 1);
    }

    void OnClearLog(object? o, RoutedEventArgs e) => LstLog.Items.Clear();
}
