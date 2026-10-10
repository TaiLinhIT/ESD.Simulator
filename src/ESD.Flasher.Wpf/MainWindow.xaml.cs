using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ESD.Protocol;

namespace ESD.Flasher.Wpf;

// Một bước trong scenario nạp
record FlashStep(Command Cmd, string PayloadText = "");

public partial class MainWindow : Window
{
    SerialPort? _port;
    uint        _seq = 1;

    CancellationTokenSource? _cts;
    readonly List<byte>      _rxBuf = new();
    TaskCompletionSource<(bool ok, ErrorCode err)>? _ackTcs;

    // Scenario mặc định — người dùng có thể chỉnh trong UI
    readonly List<FlashStep> _scenario = new()
    {
        new(Command.Heartbeat),
        new(Command.GetInfo),
        new(Command.FirmwareUpdate),
        new(Command.Reboot),
    };

    public MainWindow()
    {
        InitializeComponent();
        CbBaud.ItemsSource  = new[] { 9600, 19200, 38400, 57600, 115200 };
        CbBaud.SelectedItem = 115200;
        RefreshPorts();
        RefreshScenarioList();
    }

    // ── Serial ────────────────────────────────────────────────────────────────

    void RefreshPorts()
    {
        var all     = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
        var bt      = GetBluetoothComPorts();
        var display = all.Except(bt).ToArray();
        if (display.Length == 0) display = all;
        CbPort.ItemsSource = display;
        if (display.Length > 0) CbPort.SelectedIndex = 0;
        Log("SYS", $"Ports: [{string.Join(", ", display)}]");
    }

    static HashSet<string> GetBluetoothComPorts()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key == null) return result;
            foreach (var name in key.GetValueNames())
                if (name.Contains("BthModem", StringComparison.OrdinalIgnoreCase))
                    result.Add(key.GetValue(name)?.ToString() ?? "");
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
            ClosePort();
            _port = new SerialPort(port, (int)CbBaud.SelectedItem!, Parity.None, 8, StopBits.One)
            {
                ReadTimeout  = SerialPort.InfiniteTimeout,
                WriteTimeout = SerialPort.InfiniteTimeout,
                Handshake    = Handshake.None,
                DtrEnable    = false,
                RtsEnable    = false,
            };
            _port.DataReceived  += OnData;
            _port.ErrorReceived += (_, ev) => Dispatcher.BeginInvoke(() => Log("PORT", $"Error: {ev.EventType}"));
            _port.Open();
            TxtConn.Text = $"Connected {port}";
            Log("SYS", $"Đã kết nối {port} @ {(int)CbBaud.SelectedItem!}");
        }
        catch (Exception ex) { Log("ERROR", ex.Message); }
    }

    void ClosePort()
    {
        if (_port == null) return;
        try { _port.DataReceived -= OnData; if (_port.IsOpen) _port.Close(); _port.Dispose(); }
        catch { }
        finally { _port = null; }
    }

    void OnDisconnect(object? o, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ClosePort();
        TxtConn.Text = "Disconnected";
        Log("SYS", "Đã ngắt kết nối");
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    void OnData(object? s, SerialDataReceivedEventArgs e)
    {
        try
        {
            if (_port == null || !_port.IsOpen) return;
            int n = _port.BytesToRead;
            if (n <= 0) return;
            var buf = new byte[n];
            int r = _port.Read(buf, 0, n);
            if (r > 0) Dispatcher.BeginInvoke(() => ParseIncoming(buf[..r]));
        }
        catch { }
    }

    void ParseIncoming(byte[] data)
    {
        _rxBuf.AddRange(data);
        while (true)
        {
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
            _ackTcs?.TrySetResult((false, err));
            return;
        }
        TxtLast.Text = f.Command.ToString();
        switch (f.Command)
        {
            case Command.Ack:
                Log("ACK", $"OK — DEV={f.DeviceId} SEQ={f.Sequence}");
                _ackTcs?.TrySetResult((true, ErrorCode.Ok));
                break;
            case Command.Nack:
                var reason = f.Payload.Length > 0 ? (ErrorCode)f.Payload[0] : ErrorCode.Ok;
                Log("NACK", $"Lý do: {reason} — SEQ={f.Sequence}");
                _ackTcs?.TrySetResult((false, reason));
                break;
            default:
                Log("RESP", $"CMD={f.Command} DATA={FrameCodec.ToHex(f.Payload)}");
                _ackTcs?.TrySetResult((true, ErrorCode.Ok));
                break;
        }
    }

    // ── Scenario editor ───────────────────────────────────────────────────────

    void RefreshScenarioList()
    {
        LstScenario.Items.Clear();
        foreach (var step in _scenario)
        {
            var display = string.IsNullOrEmpty(step.PayloadText)
                ? step.Cmd.ToString()
                : $"{step.Cmd}  [{step.PayloadText}]";
            LstScenario.Items.Add(display);
        }
    }

    void OnAddStep(object? o, RoutedEventArgs e)
    {
        if (CbCommand.SelectedItem is not ComboBoxItem item) return;
        var cmdName = item.Content?.ToString() ?? "";
        if (!Enum.TryParse<Command>(cmdName, out var cmd)) return;
        var payload = TxtStepPayload.Text.Trim();
        _scenario.Add(new FlashStep(cmd, payload));
        RefreshScenarioList();
    }

    void OnRemoveStep(object? o, RoutedEventArgs e)
    {
        int idx = LstScenario.SelectedIndex;
        if (idx < 0 || idx >= _scenario.Count) return;
        _scenario.RemoveAt(idx);
        RefreshScenarioList();
    }

    void OnMoveUp(object? o, RoutedEventArgs e)
    {
        int idx = LstScenario.SelectedIndex;
        if (idx <= 0) return;
        (_scenario[idx], _scenario[idx - 1]) = (_scenario[idx - 1], _scenario[idx]);
        RefreshScenarioList();
        LstScenario.SelectedIndex = idx - 1;
    }

    void OnMoveDown(object? o, RoutedEventArgs e)
    {
        int idx = LstScenario.SelectedIndex;
        if (idx < 0 || idx >= _scenario.Count - 1) return;
        (_scenario[idx], _scenario[idx + 1]) = (_scenario[idx + 1], _scenario[idx]);
        RefreshScenarioList();
        LstScenario.SelectedIndex = idx + 1;
    }

    // ── Core: chạy scenario cho 1 device ─────────────────────────────────────

    async Task<bool> RunScenarioAsync(uint deviceId, CancellationToken ct)
    {
        Log("START", $"──── DEV={deviceId} bắt đầu scenario ({_scenario.Count} bước) ────");
        for (int i = 0; i < _scenario.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step    = _scenario[i];
            var payload = BuildPayload(step, deviceId);

            // Cập nhật step progress
            Dispatcher.Invoke(() =>
            {
                PbStep.Value    = (i + 1) * 100.0 / _scenario.Count;
                TxtStepProgress.Text = $"Bước {i + 1}/{_scenario.Count}: {step.Cmd}";
            });

            bool ok = await SendAndWaitAckAsync(deviceId, step.Cmd, payload, ct);
            if (!ok)
            {
                Log("FAIL", $"DEV={deviceId} bước {i + 1} [{step.Cmd}] không có ACK — dừng scenario");
                return false;
            }

            // Delay giữa các bước
            int delay = int.TryParse(TxtStepDelay.Text, out var d) ? d : 200;
            if (delay > 0) await Task.Delay(delay, ct);
        }
        Log("DONE", $"──── DEV={deviceId} hoàn tất scenario ✓ ────");
        return true;
    }

    // Xây dựng payload cho từng command
    byte[] BuildPayload(FlashStep step, uint deviceId)
    {
        // Nếu người dùng nhập payload text thì ưu tiên dùng
        if (!string.IsNullOrEmpty(step.PayloadText))
            return Encoding.UTF8.GetBytes(step.PayloadText);

        // Payload mặc định theo từng command (khớp với Simulator)
        return step.Cmd switch
        {
            Command.WorkerScan or
            Command.Attach     or
            Command.Detach     => Encoding.UTF8.GetBytes(TxtWorkerId.Text),

            Command.SetConfig  => Encoding.UTF8.GetBytes("{\"interval\":5}"),

            Command.AlarmTrigger => new byte[] { 0xFF }, // ManualTrigger

            Command.FirmwareUpdate => BuildFirmwarePayload(deviceId),

            _ => Array.Empty<byte>()
        };
    }

    // Payload FirmwareUpdate: version string hoặc custom
    byte[] BuildFirmwarePayload(uint deviceId)
    {
        var ver = TxtFirmwareVersion.Text.Trim();
        return string.IsNullOrEmpty(ver)
            ? Encoding.UTF8.GetBytes($"fw:dev{deviceId}:v1.0")
            : Encoding.UTF8.GetBytes(ver);
    }

    // ── Send + wait ACK ───────────────────────────────────────────────────────

    async Task<bool> SendAndWaitAckAsync(uint deviceId, Command cmd, byte[] payload,
                                         CancellationToken ct, int timeoutMs = 3000)
    {
        if (_port == null || !_port.IsOpen) { Log("ERROR", "Chưa kết nối serial"); return false; }

        _ackTcs = new TaskCompletionSource<(bool, ErrorCode)>();
        using var reg = ct.Register(() => _ackTcs.TrySetCanceled());

        try
        {
            var frame = new EsdFrame(1, FrameFlags.AckRequested, deviceId, cmd, _seq++,
                (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), payload, Array.Empty<byte>());
            var raw = FrameCodec.Encode(frame, HmacKey());

            await Task.Run(() => _port.Write(raw, 0, raw.Length), ct);
            Log("TX", $"CMD={cmd,-16} DEV={deviceId} SEQ={_seq - 1} | {FrameCodec.ToHex(raw)}");

            var timeout = Task.Delay(timeoutMs, ct);
            var done    = await Task.WhenAny(_ackTcs.Task, timeout);
            if (done == timeout) { Log("TIMEOUT", $"DEV={deviceId} CMD={cmd} SEQ={_seq - 1}"); return false; }

            var (ok, _) = await _ackTcs.Task;
            return ok;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Log("ERROR", "TX: " + ex.Message); return false; }
        finally { _ackTcs = null; }
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    async void OnFlashSingle(object? o, RoutedEventArgs e)
    {
        if (_port == null || !_port.IsOpen) { Log("ERROR", "Chưa kết nối serial"); return; }
        if (_scenario.Count == 0) { Log("ERROR", "Scenario rỗng"); return; }
        uint id = uint.TryParse(TxtDeviceId.Text, out var x) ? x : 1;
        await RunSession(new[] { id });
    }

    async void OnBulkFlash(object? o, RoutedEventArgs e)
    {
        if (_port == null || !_port.IsOpen) { Log("ERROR", "Chưa kết nối serial"); return; }
        if (_scenario.Count == 0) { Log("ERROR", "Scenario rỗng"); return; }
        var ids = ParseBulkIds();
        if (ids.Length == 0) { Log("ERROR", "Không có Device ID hợp lệ trong danh sách Bulk"); return; }
        await RunSession(ids);
    }

    void OnAbort(object? o, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("ABORT", "Người dùng đã hủy");
    }

    // ── Run session (single or bulk) ──────────────────────────────────────────

    async Task RunSession(uint[] deviceIds)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);

        PbDevice.Maximum = deviceIds.Length;
        PbDevice.Value   = 0;
        int success = 0;

        try
        {
            for (int di = 0; di < deviceIds.Length; di++)
            {
                ct.ThrowIfCancellationRequested();

                Dispatcher.Invoke(() => { PbStep.Value = 0; TxtStepProgress.Text = ""; });

                bool ok = await RunScenarioAsync(deviceIds[di], ct);
                if (ok) success++;

                Dispatcher.Invoke(() =>
                {
                    PbDevice.Value         = di + 1;
                    TxtDeviceProgress.Text = $"{di + 1} / {deviceIds.Length} thiết bị";
                    TxtStatus.Text         = ok ? $"DEV={deviceIds[di]} ✓" : $"DEV={deviceIds[di]} ✗";
                });
            }

            var final = $"Xong: {success}/{deviceIds.Length} thiết bị thành công";
            TxtStatus.Text = final;
            Log("SESSION", final);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Aborted";
            Log("ABORT", "Session bị hủy");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Error";
            Log("ERROR", ex.Message);
        }
        finally { SetBusy(false); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    uint[] ParseBulkIds() =>
        TxtBulkIds.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => uint.TryParse(l, out var v) ? (uint?)v : null)
            .Where(v => v.HasValue).Select(v => v!.Value).ToArray();

    byte[] HmacKey() => FrameCodec.FromHex(TxtKey.Text);

    void SetBusy(bool busy)
    {
        BtnFlash.IsEnabled = !busy;
        BtnBulk.IsEnabled  = !busy;
        BtnAbort.IsEnabled = busy;
    }

    void Log(string tag, string msg)
    {
        LstLog.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} [{tag,-7}] {msg}");
        while (LstLog.Items.Count > 1000) LstLog.Items.RemoveAt(LstLog.Items.Count - 1);
    }

    void OnClearLog(object? o, RoutedEventArgs e) => LstLog.Items.Clear();
}
