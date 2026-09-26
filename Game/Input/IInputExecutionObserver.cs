using SixLabors.ImageSharp;
using SharedLib;
using System;

namespace Game;

public interface IInputExecutionObserver
{
    void OnKeyboard(ConsoleKey key, bool down, int? durationMs = null, ModifierKey modifier = ModifierKey.None);
    void OnMouse(string operation, Point point);
    void OnText(char character);
}
