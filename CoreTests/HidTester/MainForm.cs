using HidShared;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HidTester;

internal sealed class MainForm : Form
{
    private readonly ComboBox ports = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Button connectButton = new() { Text = "Connect", AutoSize = true };
    private readonly Label connection = new() { Text = "Disconnected", AutoSize = true, ForeColor = Color.Firebrick };
    private readonly NumericUpDown holdMs = new() { Minimum = 20, Maximum = 10000, Value = 500, Width = 90 };
    private readonly ComboBox holdKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox testKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    private readonly ComboBox modifier = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly NumericUpDown targetX = new() { Minimum = -32000, Maximum = 32000, Width = 85 };
    private readonly NumericUpDown targetY = new() { Minimum = -32000, Maximum = 32000, Width = 85 };
    private readonly TextBox textInput = new() { Text = "/help", Width = 170 };
    private readonly NumericUpDown mouseX = new() { Minimum = -2000, Maximum = 2000, Value = 40, Width = 75 };
    private readonly NumericUpDown mouseY = new() { Minimum = -2000, Maximum = 2000, Value = 20, Width = 75 };
    private readonly NumericUpDown wheelValue = new() { Minimum = -20, Maximum = 20, Value = 1, Width = 75 };
    private readonly RichTextBox s3Log = LogBox();
    private readonly RichTextBox windowsLog = LogBox();
    private readonly ListView results = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill };
    private readonly WindowsHidMonitor monitor;
    private HidClient? client;
    private bool busy;
    private bool connectionLost;

    public MainForm()
    {
        Text = "ESP32-S3 HID Tester";
        Width = 1120; Height = 760; MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        monitor = new WindowsHidMonitor();
        monitor.EventObserved += OnWindowsEvent;
        BuildUi();
        RefreshPorts();
        object[] keyNames = ["W", "A", "S", "D", "Q", "E", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
            "Space", "Tab", "Escape", "Enter", "Backspace", "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
            "Up", "Down", "Left", "Right", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "Numpad 0", "Numpad 1", "Numpad 2", "Numpad 3", "Numpad 4", "Numpad 5", "Numpad 6", "Numpad 7", "Numpad 8", "Numpad 9",
            "Numpad *", "Numpad +", "Numpad -", "Numpad /", "Numpad ."];
        holdKey.Items.AddRange(keyNames);
        holdKey.SelectedIndex = 0;
        testKey.Items.AddRange(keyNames);
        testKey.SelectedIndex = 0;
        modifier.Items.AddRange(["None", "Shift", "Ctrl", "Alt"]);
        modifier.SelectedIndex = 0;
        SetTargetToCursor();
        connectButton.Click += async (_, _) => await ToggleConnectionAsync();
        FormClosed += async (_, _) =>
        {
            monitor.Dispose();
            if (client is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
                    await client.ReleaseAllAsync(timeout.Token);
                }
                catch { }
                try { await client.DisposeAsync(); } catch { }
            }
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        top.Controls.Add(new Label { Text = "CDC COM", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        top.Controls.Add(ports);
        var refresh = new Button { Text = "Refresh", AutoSize = true }; refresh.Click += (_, _) => RefreshPorts(); top.Controls.Add(refresh);
        top.Controls.Add(connectButton); top.Controls.Add(connection);
        root.Controls.Add(top, 0, 0);

        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        workspace.Controls.Add(BuildControls(), 0, 0);
        var logs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        logs.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        logs.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        logs.Controls.Add(Labeled("CDC Control (PC commands / S3 ACK; UART logs on COM12)", s3Log), 0, 0);
        logs.Controls.Add(Labeled("Windows HID Events (low-level hook)", windowsLog), 0, 1);
        workspace.Controls.Add(logs, 1, 0);
        root.Controls.Add(workspace, 0, 1);
        results.Columns.Add("Test", 210); results.Columns.Add("Result", 120); results.Columns.Add("Details", 190);
        root.Controls.Add(results, 0, 2);
        Controls.Add(root);
    }

    private FlowLayoutPanel BuildControls()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        panel.Controls.Add(Section("Keyboard HID"));
        var keys = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(510, 0) };
        foreach (string key in new[] { "W", "A", "S", "D", "Space" })
        {
            string captured = key;
            var button = new Button { Text = captured, Width = 70, Height = 38 };
            button.Click += async (_, _) => await TestKeyAsync(captured, VirtualKey(captured), 100, false);
            keys.Controls.Add(button);
        }
        panel.Controls.Add(keys);
        var hold = new FlowLayoutPanel { AutoSize = true };
        hold.Controls.Add(new Label { Text = "Hold", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        hold.Controls.Add(holdKey); hold.Controls.Add(holdMs);
        var holdButton = new Button { Text = "Run Hold", AutoSize = true };
        holdButton.Click += async (_, _) => await TestKeyAsync(holdKey.Text, VirtualKey(holdKey.Text), (int)holdMs.Value, true);
        hold.Controls.Add(holdButton); panel.Controls.Add(hold);
        var chosenKey = new FlowLayoutPanel { AutoSize = true };
        chosenKey.Controls.Add(testKey); chosenKey.Controls.Add(modifier);
        var chosenButton = new Button { Text = "Test Key / Combo", AutoSize = true };
        chosenButton.Click += async (_, _) => await TestSelectedKeyAsync();
        chosenKey.Controls.Add(chosenButton); panel.Controls.Add(chosenKey);
        var combo = new Button { Text = "Alt + Home", AutoSize = true };
        combo.Click += async (_, _) => await TestComboAsync(); panel.Controls.Add(combo);
        var overlap = new Button { Text = "W + Space overlap", AutoSize = true };
        overlap.Click += async (_, _) => await TestOverlappingKeysAsync(); panel.Controls.Add(overlap);
        var cancel = new Button { Text = "Cancel Hold", AutoSize = true };
        cancel.Click += async (_, _) => await TestCancelHoldAsync(); panel.Controls.Add(cancel);
        var disconnectHeld = new Button { Text = "Disconnect While Held", AutoSize = true };
        disconnectHeld.Click += async (_, _) => await TestDisconnectWhileHeldAsync(); panel.Controls.Add(disconnectHeld);
        var text = new FlowLayoutPanel { AutoSize = true }; text.Controls.Add(textInput);
        var sendText = new Button { Text = "Send Text", AutoSize = true };
        sendText.Click += async (_, _) => await TestTextAsync(); text.Controls.Add(sendText); panel.Controls.Add(text);
        var release = new Button { Text = "Release All", AutoSize = true };
        release.Click += async (_, _) => await TestReleaseAllAsync(); panel.Controls.Add(release);

        panel.Controls.Add(Section("Mouse HID (relative X/Y)"));
        var move = new FlowLayoutPanel { AutoSize = true };
        move.Controls.Add(new Label { Text = "X", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); move.Controls.Add(mouseX);
        move.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); move.Controls.Add(mouseY);
        var moveButton = new Button { Text = "Mouse Move", AutoSize = true };
        moveButton.Click += async (_, _) => await TestMouseMoveAsync(); move.Controls.Add(moveButton); panel.Controls.Add(move);
        var target = new FlowLayoutPanel { AutoSize = true };
        target.Controls.Add(new Label { Text = "Target X", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); target.Controls.Add(targetX);
        target.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); target.Controls.Add(targetY);
        var current = new Button { Text = "Use Cursor", AutoSize = true }; current.Click += (_, _) => SetTargetToCursor(); target.Controls.Add(current);
        panel.Controls.Add(target);
        var absolute = new FlowLayoutPanel { AutoSize = true };
        var position = new Button { Text = "Move to Target", AutoSize = true }; position.Click += async (_, _) => await TestAbsoluteMoveAsync(); absolute.Controls.Add(position);
        var targetLeft = new Button { Text = "Target + Left Click", AutoSize = true }; targetLeft.Click += async (_, _) => await TestTargetClickAsync(1); absolute.Controls.Add(targetLeft);
        var targetRight = new Button { Text = "Target + Right Click", AutoSize = true }; targetRight.Click += async (_, _) => await TestTargetClickAsync(2); absolute.Controls.Add(targetRight);
        panel.Controls.Add(absolute);
        var clicks = new FlowLayoutPanel { AutoSize = true };
        var left = new Button { Text = "Left Click", AutoSize = true }; left.Click += async (_, _) => await TestClickAsync(1, "Left Click"); clicks.Controls.Add(left);
        var right = new Button { Text = "Right Click", AutoSize = true }; right.Click += async (_, _) => await TestClickAsync(2, "Right Click"); clicks.Controls.Add(right); panel.Controls.Add(clicks);
        var wheel = new FlowLayoutPanel { AutoSize = true }; wheel.Controls.Add(new Label { Text = "Wheel", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); wheel.Controls.Add(wheelValue);
        var wheelButton = new Button { Text = "Send Wheel", AutoSize = true }; wheelButton.Click += async (_, _) => await TestWheelAsync(); wheel.Controls.Add(wheelButton); panel.Controls.Add(wheel);
        var all = new Button { Text = "Run All", Width = 120, Height = 40 }; all.Click += async (_, _) => await RunAllAsync(); panel.Controls.Add(all);
        var foreground = new Button { Text = "Foreground Check (3s)", AutoSize = true };
        foreground.Click += async (_, _) => await TestForegroundAsync(); panel.Controls.Add(foreground);
        return panel;
    }

    private async Task ToggleConnectionAsync()
    {
        if (client is not null && !connectionLost)
        {
            connectButton.Enabled = false;
            HidClient closingClient = client;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
                await closingClient.ReleaseAllAsync(timeout.Token);
            }
            catch (Exception ex) { Append(s3Log, "PC  Release All during disconnect: " + ex.Message); }
            finally
            {
                try { await closingClient.DisposeAsync(); }
                catch (Exception ex) { Append(s3Log, "PC  Disconnect: " + ex.Message); }
                finally
                {
                    if (ReferenceEquals(client, closingClient)) client = null;
                    connectionLost = false;
                    connection.Text = "Disconnected"; connection.ForeColor = Color.Firebrick;
                    connectButton.Text = "Connect"; connectButton.Enabled = true;
                }
            }
            return;
        }
        if (client is not null)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { Append(s3Log, "PC  Cleanup after connection loss: " + ex.Message); }
            client = null;
            connectionLost = false;
        }
        if (ports.SelectedItem is not string portName)
        {
            connection.Text = "Select a CDC COM port"; connection.ForeColor = Color.Firebrick;
            Append(s3Log, "ERR Select an ESP32 CDC COM port before connecting.");
            return;
        }
        connectButton.Enabled = false;
        try
        {
            client = new HidClient(portName);
            connectionLost = false;
            client.PcLog += line => Ui(() => Append(s3Log, "PC  " + line));
            client.ConnectionLost += ex => Ui(() =>
            {
                connectionLost = true;
                connection.Text = "Serial connection lost";
                connection.ForeColor = Color.Firebrick;
                connectButton.Text = "Connect";
                Append(s3Log, "ERR " + ex.Message);
            });
            connection.Text = $"Serial open: {portName} (ACK checked per report)";
            connection.ForeColor = Color.ForestGreen;
            connectButton.Text = "Disconnect";
            Append(s3Log, $"Connected to {portName} at 115200 baud. HID response has not been checked yet.");
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                try { await client.DisposeAsync(); } catch { }
            }
            client = null;
            connectionLost = false;
            connection.Text = "Disconnected"; connection.ForeColor = Color.Firebrick;
            connectButton.Text = "Connect";
            Append(s3Log, "ERR Connect failed: " + ex.Message);
        }
        finally { connectButton.Enabled = true; }
    }

    private async Task TestKeyAsync(string label, int vk, int duration, bool isHold)
    {
        string testName = isHold ? $"Key Hold {label} {duration}ms" : $"Keyboard {label}";
        await RunTestAsync(testName,
            async () => { await RequireClient().HoldAsync(vk, duration); return await CheckKeyPair(vk, duration); });
    }

    private async Task TestComboAsync() => await RunTestAsync("Key Combo Alt+Home", async () =>
    {
        long start = WindowsHidMonitor.Now; await RequireClient().AltHomeAsync(); await Task.Delay(120);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        bool pass = HasPair(ev, 0x12) && HasPair(ev, 0x24);
        return (pass, pass ? "Windows saw Alt and Home down/up" : "Windows event pair incomplete");
    });

    private async Task TestSelectedKeyAsync()
    {
        string key = testKey.Text;
        int vk = VirtualKey(key);
        int modifierVk = modifier.Text switch { "Shift" => 0x10, "Ctrl" => 0x11, "Alt" => 0x12, _ => 0 };
        if (modifierVk == 0) { await TestKeyAsync(key, vk, 100, false); return; }
        await TestModifiedKeyAsync($"{modifier.Text}+{key}", vk, modifierVk);
    }

    private async Task TestModifiedKeyAsync(string name, int vk, int modifierVk) => await RunTestAsync("Combo " + name, async () =>
    {
        long start = WindowsHidMonitor.Now;
        try
        {
            await RequireClient().KeyDownAsync(modifierVk);
            await RequireClient().HoldAsync(vk, 100);
        }
        finally { await RequireClient().KeyUpAsync(modifierVk); }
        await Task.Delay(100);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        int modDown = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyDown && MatchesVirtualKey(e.Value, modifierVk));
        int keyDown = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyDown && e.Value == vk);
        int keyUp = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyUp && e.Value == vk);
        int modUp = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyUp && MatchesVirtualKey(e.Value, modifierVk));
        bool pass = modDown >= 0 && modDown < keyDown && keyDown < keyUp && keyUp < modUp;
        return (pass, pass ? "Windows saw modifier and key in press/release order" : "Windows combo sequence incomplete or out of order");
    });

    private async Task TestOverlappingKeysAsync() => await RunTestAsync("W + Space overlap", async () =>
    {
        long start = WindowsHidMonitor.Now;
        try
        {
            await RequireClient().KeyDownAsync('W');
            await Task.Delay(80);
            await RequireClient().KeyDownAsync(0x20);
            await Task.Delay(80);
            await RequireClient().KeyUpAsync(0x20);
            await Task.Delay(80);
            await RequireClient().KeyUpAsync('W');
        }
        finally { await RequireClient().ReleaseAllAsync(); }
        await Task.Delay(100);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        int wDown = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyDown && e.Value == 'W');
        int spaceDown = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyDown && e.Value == 0x20);
        int spaceUp = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyUp && e.Value == 0x20);
        int wUp = Array.FindIndex(ev, e => e.Kind == HidEventKind.KeyUp && e.Value == 'W');
        bool pass = wDown >= 0 && wDown < spaceDown && spaceDown < spaceUp && spaceUp < wUp;
        return (pass, pass ? "W stayed down through Space press/release" : "Overlapping key sequence incomplete or out of order");
    });

    private async Task TestCancelHoldAsync() => await RunTestAsync("Cancel Hold W", async () =>
    {
        long start = WindowsHidMonitor.Now;
        using var cancel = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            Task<int> hold = RequireClient().HoldAsync('W', 1000, cancel.Token);
            for (int attempt = 0; attempt < 100 && !monitor.Since(start).Any(e => !e.Injected && e.Kind == HidEventKind.KeyDown && e.Value == 'W'); attempt++)
                await Task.Delay(10);
            await Task.Delay(150);
            cancel.Cancel();
            try { await hold; }
            catch (OperationCanceledException) { cancelled = true; }
        }
        finally { await RequireClient().ReleaseAllAsync(); }
        await Task.Delay(100);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        bool pass = cancelled && HasPair(ev, 'W');
        return (pass, pass ? "Cancelled hold; Windows saw W release" : "Cancellation or Windows W release missing");
    });

    private async Task TestDisconnectWhileHeldAsync() => await RunTestAsync("Disconnect While Held", async () =>
    {
        long start = WindowsHidMonitor.Now;
        await RequireClient().KeyDownAsync('W');
        await Task.Delay(100);
        await ToggleConnectionAsync();
        await Task.Delay(120);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        bool pass = client is null && HasPair(ev, 'W');
        return (pass, pass ? "Disconnect released W; reconnect to continue" : "Disconnect or Windows W release missing");
    });

    private void SetTargetToCursor()
    {
        System.Drawing.Point point = Cursor.Position;
        targetX.Value = Math.Clamp(point.X, (int)targetX.Minimum, (int)targetX.Maximum);
        targetY.Value = Math.Clamp(point.Y, (int)targetY.Minimum, (int)targetY.Maximum);
    }

    private async Task TestAbsoluteMoveAsync() => await RunTestAsync("Move to Target", async () =>
    {
        int x = (int)targetX.Value, y = (int)targetY.Value;
        long start = WindowsHidMonitor.Now;
        await RequireClient().MoveCursorAsync(x, y);
        await Task.Delay(100);
        System.Drawing.Point actual = Cursor.Position;
        bool observed = monitor.Since(start).Any(e => !e.Injected && e.Kind == HidEventKind.MouseMove);
        bool pass = observed && Math.Abs(actual.X - x) <= 2 && Math.Abs(actual.Y - y) <= 2;
        return (pass, $"Target ({x},{y}); Windows cursor ({actual.X},{actual.Y}); move event {(observed ? "seen" : "missing")}");
    });

    private async Task TestTargetClickAsync(byte button) => await RunTestAsync(button == 1 ? "Target + Left Click" : "Target + Right Click", async () =>
    {
        int x = (int)targetX.Value, y = (int)targetY.Value;
        long start = WindowsHidMonitor.Now;
        await RequireClient().MoveCursorAsync(x, y);
        await RequireClient().ClickAsync(button);
        await Task.Delay(100);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        HidEventKind down = button == 1 ? HidEventKind.LeftDown : HidEventKind.RightDown;
        HidEventKind up = button == 1 ? HidEventKind.LeftUp : HidEventKind.RightUp;
        HidEvent? pressed = ev.FirstOrDefault(e => e.Kind == down);
        bool pass = pressed is not null && ev.Any(e => e.Kind == up && e.Timestamp >= pressed.Timestamp)
            && Math.Abs(pressed.X - x) <= 2 && Math.Abs(pressed.Y - y) <= 2;
        return (pass, pressed is null ? "Windows click event missing" : $"Windows click at ({pressed.X},{pressed.Y}); target ({x},{y})");
    });

    private async Task TestForegroundAsync()
    {
        if (busy || client is null || connectionLost) return;
        busy = true;
        const string name = "Foreground Window";
        AddResult(name, "RUNNING", "Activate the game window within 3 seconds");
        try
        {
            Append(s3Log, "PC  Activate the target window now; sending W after 3 seconds.");
            await Task.Delay(3000);
            IntPtr window = GetForegroundWindow();
            long start = WindowsHidMonitor.Now;
            await RequireClient().HoldAsync('W', 100);
            await Task.Delay(100);
            bool observed = HasPair(monitor.Since(start).Where(e => !e.Injected).ToArray(), 'W');
            ReplaceResult(name, observed ? "MANUAL CHECK" : "FAIL", $"Foreground HWND 0x{window.ToInt64():X}; Windows W events {(observed ? "seen" : "missing")}. Confirm the target app reacted.");
        }
        catch (Exception ex) { ReplaceResult(name, "FAIL", ex.Message); }
        finally { busy = false; }
    }

    private async Task TestTextAsync() => await RunTestAsync("Text " + textInput.Text, async () =>
    {
        string text = textInput.Text;
        long start = WindowsHidMonitor.Now; await RequireClient().SendTextAsync(text); await Task.Delay(150);
        var ev = monitor.Since(start).Where(e => !e.Injected && e.Kind == HidEventKind.KeyDown).ToArray();
        int[] expected = text.Select(ExpectedTextVirtualKey).ToArray();
        int cursor = 0;
        foreach (HidEvent keyEvent in ev)
            if (cursor < expected.Length && keyEvent.Value == expected[cursor]) cursor++;
        bool pass = expected.Length > 0 && cursor == expected.Length;
        return (pass, pass ? $"Windows saw all {expected.Length} requested characters" : $"Windows saw {cursor}/{expected.Length} requested characters");
    });

    private async Task TestMouseMoveAsync() => await RunTestAsync("Mouse Move", async () =>
    {
        long start = WindowsHidMonitor.Now; await RequireClient().MoveRelativeAsync((int)mouseX.Value, (int)mouseY.Value); await Task.Delay(120);
        HidEvent? move = monitor.Since(start).FirstOrDefault(e => !e.Injected && e.Kind == HidEventKind.MouseMove);
        return (move is not null, move is null ? "Windows mouse move not observed" : $"Windows cursor event at {move.X},{move.Y}");
    });

    private async Task TestClickAsync(byte button, string title) => await RunTestAsync(title, async () =>
    {
        long start = WindowsHidMonitor.Now; await RequireClient().ClickAsync(button); await Task.Delay(100);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray();
        HidEventKind down = button == 1 ? HidEventKind.LeftDown : HidEventKind.RightDown;
        HidEventKind up = button == 1 ? HidEventKind.LeftUp : HidEventKind.RightUp;
        bool pass = ev.Any(e => e.Kind == down) && ev.Any(e => e.Kind == up);
        return (pass, pass ? "Windows saw button down/up" : "Windows mouse button events incomplete");
    });

    private async Task TestWheelAsync() => await RunTestAsync("Wheel", async () =>
    {
        long start = WindowsHidMonitor.Now; await RequireClient().MoveRelativeAsync(0, 0, (int)wheelValue.Value); await Task.Delay(120);
        HidEvent? wheel = monitor.Since(start).FirstOrDefault(e => !e.Injected && e.Kind == HidEventKind.Wheel && e.Value != 0);
        return (wheel is not null, wheel is null ? "Windows wheel event not observed" : $"Windows wheel delta {wheel.Value}");
    });

    private async Task TestReleaseAllAsync() => await RunTestAsync("Release All", async () =>
    {
        long start = WindowsHidMonitor.Now; await RequireClient().KeyDownAsync(0x57); await Task.Delay(80); await RequireClient().ReleaseAllAsync(); await Task.Delay(120);
        var ev = monitor.Since(start).Where(e => !e.Injected).ToArray(); bool pass = ev.Any(e => e.Kind == HidEventKind.KeyDown && e.Value == 0x57) && ev.Any(e => e.Kind == HidEventKind.KeyUp && e.Value == 0x57);
        return (pass, pass ? "Windows saw W release" : "Windows release event missing");
    });

    private async Task RunAllAsync()
    {
        await TestKeyAsync("W", 0x57, 100, false);
        await TestKeyAsync("W", 0x57, (int)holdMs.Value, true);
        await TestOverlappingKeysAsync();
        await TestComboAsync();
        await TestModifiedKeyAsync("Shift+PageDown", 0x22, 0x10);
        await TestModifiedKeyAsync("Ctrl+Tab", 0x09, 0x11);
        foreach (string key in new[] { "Q", "E", "1", "0", "Tab", "Escape", "End", "Up", "F1", "F12", "Numpad *", "Numpad +", "Numpad -" })
            await TestKeyAsync(key, VirtualKey(key), 100, false);
        await TestCancelHoldAsync();
        await TestMouseMoveAsync();
        await TestClickAsync(1, "Left Click");
        await TestClickAsync(2, "Right Click");
        await TestWheelAsync();
        await TestReleaseAllAsync();
        AddResult("Foreground Window", "MANUAL CHECK", "Use Foreground Check (3s) with the target game active.");
        AddResult("Target Click", "MANUAL CHECK", "Choose a safe screen coordinate, then use Target + Click.");
        AddResult("Disconnect While Held", "MANUAL CHECK", "Run separately; it closes the CDC connection.");
    }

    private async Task<(bool pass, string details)> CheckKeyPair(int vk, int requestedMs)
    {
        await Task.Delay(100);
        var hits = monitor.Since(testStart).Where(e => !e.Injected && e.Value == vk && e.Kind is HidEventKind.KeyDown or HidEventKind.KeyUp).ToArray();
        var down = hits.FirstOrDefault(e => e.Kind == HidEventKind.KeyDown); var up = hits.FirstOrDefault(e => e.Kind == HidEventKind.KeyUp);
        if (down is null || up is null) return (false, "Windows KEY_DOWN/KEY_UP event missing");
        double elapsed = Stopwatch.GetElapsedTime(down.Timestamp, up.Timestamp).TotalMilliseconds;
        bool withinTolerance = Math.Abs(elapsed - requestedMs) <= 150;
        return (withinTolerance, $"Windows duration {elapsed:F0}ms (requested {requestedMs}ms)");
    }

    private long testStart;
    private async Task RunTestAsync(string name, Func<Task<(bool pass, string details)>> action)
    {
        if (busy) return;
        if (client is null || connectionLost)
        {
            Append(s3Log, "ERR Connect to the ESP32-S3 CDC port before running a test.");
            return;
        }
        busy = true; testStart = WindowsHidMonitor.Now;
        AddResult(name, "RUNNING", "Waiting for S3 ACK and Windows events");
        try
        {
            Append(s3Log, "TX  " + name);
            var (pass, details) = await action();
            ReplaceResult(name, pass ? "PASS" : "FAIL", details);
        }
        catch (Exception ex)
        {
            ReplaceResult(name, "FAIL", ex.Message); Append(s3Log, "ERR " + ex.Message);
            try { await client.ReleaseAllAsync(); } catch { }
        }
        finally { busy = false; }
    }

    private static bool HasPair(IReadOnlyList<HidEvent> events, int vk) =>
        events.Any(e => e.Kind == HidEventKind.KeyDown && MatchesVirtualKey(e.Value, vk)) &&
        events.Any(e => e.Kind == HidEventKind.KeyUp && MatchesVirtualKey(e.Value, vk));

    // Low-level Windows hooks report side-specific modifier VKs for the generic VK sent by HidClient.
    private static bool MatchesVirtualKey(int observed, int requested) => requested switch
    {
        0x10 => observed is 0x10 or 0xA0 or 0xA1, // Shift
        0x11 => observed is 0x11 or 0xA2 or 0xA3, // Ctrl
        0x12 => observed is 0x12 or 0xA4 or 0xA5, // Alt
        _ => observed == requested
    };
    private HidClient RequireClient() => client ?? throw new InvalidOperationException("Connect to the ESP32-S3 first.");
    private static int VirtualKey(string key) => key switch
    {
        "Space" => 0x20, "Tab" => 0x09, "Escape" => 0x1B, "Enter" => 0x0D,
        "Backspace" => 0x08, "Insert" => 0x2D, "Delete" => 0x2E,
        "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22,
        "Up" => 0x26, "Down" => 0x28, "Left" => 0x25, "Right" => 0x27,
        "Numpad *" => 0x6A, "Numpad +" => 0x6B, "Numpad -" => 0x6D, "Numpad /" => 0x6F, "Numpad ." => 0x6E,
        _ when key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 12 => 0x6F + function,
        _ when key.StartsWith("Numpad ") && int.TryParse(key.AsSpan(7), out int digit) && digit is >= 0 and <= 9 => 0x60 + digit,
        _ when key.Length == 1 => char.ToUpperInvariant(key[0]),
        _ => throw new NotSupportedException($"Unknown test key '{key}'.")
    };
    private static int ExpectedTextVirtualKey(char c)
    {
        if (char.IsAsciiLetterOrDigit(c)) return char.ToUpperInvariant(c);
        return c switch
        {
            ' ' => 0x20, '\n' => 0x0D, '-' or '_' => 0xBD, '=' or '+' => 0xBB,
            '[' or '{' => 0xDB, ']' or '}' => 0xDD, '\\' or '|' => 0xDC,
            ';' or ':' => 0xBA, '\'' or '"' => 0xDE, '`' or '~' => 0xC0,
            ',' or '<' => 0xBC, '.' or '>' => 0xBE, '/' or '?' => 0xBF,
            '!' => '1', '@' => '2', '#' => '3', '$' => '4', '%' => '5', '^' => '6',
            '&' => '7', '*' => '8', '(' => '9', ')' => '0',
            _ => throw new NotSupportedException($"No Windows key event mapping for '{c}'.")
        };
    }

    private void OnWindowsEvent(HidEvent ev) => Ui(() => Append(windowsLog, $"WIN  {ev.Kind,-10} {(ev.Value == 0 ? "" : ev.Value.ToString("X2"))} @ {ev.X},{ev.Y}{(ev.Injected ? " [injected]" : " [non-injected]")}"));
    private void RefreshPorts()
    {
        string? selected = ports.SelectedItem as string;
        ports.Items.Clear(); ports.Items.AddRange(SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        if (selected is not null && ports.Items.Contains(selected)) ports.SelectedItem = selected;
        else if (ports.Items.Count > 0) ports.SelectedIndex = 0;
    }
    private void AddResult(string name, string status, string details) => results.Items.Add(new ListViewItem([name, status, details]));
    private void ReplaceResult(string name, string status, string details)
    {
        for (int i = results.Items.Count - 1; i >= 0; i--)
            if (results.Items[i].Text == name && results.Items[i].SubItems[1].Text == "RUNNING")
            { results.Items[i].SubItems[1].Text = status; results.Items[i].SubItems[2].Text = details; return; }
        AddResult(name, status, details);
    }
    private static GroupBox Labeled(string title, Control body)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        body.Dock = DockStyle.Fill; group.Controls.Add(body); return group;
    }
    private static Label Section(string text) => new() { Text = text, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), AutoSize = true, Margin = new Padding(2, 12, 2, 4) };
    private static RichTextBox LogBox() => new() { ReadOnly = true, Dock = DockStyle.Fill, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, Font = new Font("Consolas", 9) };
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private void Ui(Action action) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(action); else action(); }
    private static void Append(RichTextBox box, string line)
    {
        box.AppendText($"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        if (box.TextLength > 250_000)
        {
            box.Select(0, box.TextLength - 200_000);
            box.SelectedText = string.Empty;
        }
        box.SelectionStart = box.TextLength; box.ScrollToCaret();
    }
}
