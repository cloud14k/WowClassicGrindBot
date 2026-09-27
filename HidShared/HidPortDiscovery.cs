using System;
using System.IO.Ports;
using System.Linq;

namespace HidShared;

public static class HidPortDiscovery
{
    public static string[] GetPortNames() => SerialPort.GetPortNames()
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
