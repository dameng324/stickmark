using System;

namespace Stickmark.Services;

/// Watcher (vault mode only) that automatically reflects external changes (.md) in the vault folder during execution.
public interface IVaultWatcher : IDisposable
{
    /// Start monitoring. Immediately after FSW activation, one matching cycle is performed to retrieve changes between start and watch.
    void Start(string vaultFolder);
}
