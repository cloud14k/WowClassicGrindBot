using System.Reflection;
using Game;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;

string directory = Path.Combine(Path.GetTempPath(), "InputBackendRegression-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    string path = Path.Combine(directory, "input-backend.json");
    File.WriteAllText(path, "{\"Backend\":\"Hid\",\"Port\":\"COM5\"}");
    var settings = new InputBackendSettings(path);
    var oldInput = new FakeInput { FailDispose = true };
    var windowsInput = new FakeInput();
    using (var router = CreateRouter(settings, (backend, _) => backend == "Hid" ? oldInput : windowsInput))
    {
        settings.Save("Windows", "COM5");
        Require(oldInput.Disposed, "Old backend cleanup was attempted");
        Require(!windowsInput.Disposed, "Replacement survived old backend cleanup failure");
        Require(new InputBackendSettings(path).Backend == "Windows", "Restart loads Windows after cleanup failure");
        ((IInput)router).KeyDown(65);
        Require(windowsInput.KeyDownCount == 1, "Input is routed to the working replacement");
    }
    Console.WriteLine("PASS: HID cleanup failure does not roll back Windows or dispose the replacement.");

    File.WriteAllText(path, "{\"Backend\":\"Windows\",\"Port\":\"COM5\"}");
    settings = new InputBackendSettings(path);
    windowsInput = new FakeInput();
    using (var router = CreateRouter(settings, (backend, _) => backend == "Windows"
        ? windowsInput = new FakeInput() : throw new IOException("Device unavailable")))
    {
        try { settings.Save("Hid", "COM6"); throw new Exception("Expected switch failure"); }
        catch (IOException) { }
        Require(settings.Backend == "Windows", "Failed creation restores in-memory selection");
        Require(new InputBackendSettings(path).Backend == "Windows", "Failed creation preserves persisted selection");
        ((IInput)router).KeyDown(65);
        Require(windowsInput.KeyDownCount == 1 && !windowsInput.Disposed, "Restored Windows backend remains usable");
    }
    Console.WriteLine("PASS: Failed replacement creation preserves the previous backend and configuration.");
}
finally { Directory.Delete(directory, recursive: true); }

static IDisposable CreateRouter(InputBackendSettings settings, Func<string, string, IInput> factory)
{
    Type type = typeof(WowProcessInput).Assembly.GetType("Game.InputBackendRouter", throwOnError: true)!;
    ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
        null, [typeof(InputBackendSettings), typeof(Func<string, string, IInput>), typeof(ILogger)], null)!;
    return (IDisposable)constructor.Invoke([settings, factory, NullLogger.Instance]);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeInput : IInput, IDisposable
{
    public bool FailDispose { get; init; }
    public bool Disposed { get; private set; }
    public int KeyDownCount { get; private set; }
    public void Dispose()
    {
        Disposed = true;
        if (FailDispose) throw new IOException("Simulated failed HID cleanup");
    }
    public void KeyDown(int key)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        KeyDownCount++;
    }
    public void KeyUp(int key) { }
    public int PressRandom(int key, int milliseconds) => milliseconds;
    public int PressRandom(int key, int milliseconds, CancellationToken token) => milliseconds;
    public void PressFixed(int key, int milliseconds, CancellationToken token) { }
    public void SetCursorPos(Point p) { }
    public void RightClick(Point p) { }
    public void LeftClick(Point p) { }
    public void SendText(string text) { }
}
