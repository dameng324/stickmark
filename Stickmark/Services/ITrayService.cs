using System;

namespace Stickmark.Services;

public interface ITrayService : IDisposable
{
    void Initialize();

    /// Tray balloon notification (best-effort) — Silently ignored if tray initialization fails.
    void ShowNotification(string title, string message);
}
