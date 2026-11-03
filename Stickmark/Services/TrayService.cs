using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Utils;

namespace Stickmark.Services;

public sealed class TrayService : ITrayService
{
    private readonly IWindowManager _windowManager;
    private readonly IBackupService _backupService;
    private readonly IAutoStartService _autoStartService;
    private readonly IUpdateService _updateService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<TrayService> _logger;

    private TaskbarIcon? _icon;
    private MenuItem? _autoStartMenu;
    private MenuItem? _openFolderMenu;
    private MenuItem? _obsidianMenu;
    private MenuItem? _vsCodeMenu;

    public TrayService(
        IWindowManager windowManager,
        IBackupService backupService,
        IAutoStartService autoStartService,
        IUpdateService updateService,
        ISettingsService settingsService,
        ILogger<TrayService> logger)
    {
        _windowManager = windowManager;
        _backupService = backupService;
        _autoStartService = autoStartService;
        _updateService = updateService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public void Initialize()
    {
        try
        {
            _icon = new TaskbarIcon
            {
                // ImageSource asynchronous conversion path (OnIconSourceChanged → ToIconAsync)
                // Depending on the environment, an unhandled exception may be thrown to the dispatcher, leaving the tray empty.
                // In some cases, we use a synchronous path that directly assigns System.Drawing.Icon.
                Icon = IconFactory.CreateTrayIcon(16),
                ToolTipText = Strings.Tray_Tooltip,
                NoLeftClickDelay = true,
            };
            _icon.LeftClickCommand = new DelegateCommand(_ => _windowManager.ToggleAllVisible());
            _icon.DoubleClickCommand = new DelegateCommand(_ => _windowManager.CreateAndShowNew());
            _icon.ContextMenu = BuildContextMenu();

            // The Loaded event occurs only when TaskbarIcon is attached to the visual tree.
            // Internal Shell_NotifyIcon call is executed. If you just add new in the code behind, it will appear in the tray.
            // Since it never appears, explicitly call ForceCreate().
            _icon.ForceCreate(enablesEfficiencyMode: false);
            _logger.LogInformation("Tray icon created");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize tray icon");
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItem(Strings.Tray_NewNote, () => _windowManager.CreateAndShowNew()));
        menu.Items.Add(BuildTemplateMenu());
        menu.Items.Add(MenuItem(Strings.Tray_OpenMarkdownFile, OpenMarkdownFile));
        menu.Items.Add(MenuItem(Strings.Tray_AllNotes, () => _windowManager.OpenNotesList()));
        menu.Items.Add(MenuItem(Strings.Tray_TodoView, () => _windowManager.OpenTodoView()));
        menu.Items.Add(MenuItem(Strings.Tray_QuickSwitcher, () => _windowManager.OpenQuickSwitcher()));
        menu.Items.Add(MenuItem(Strings.Tray_Settings, () => _windowManager.OpenSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Strings.Tray_ShowAll, () => _windowManager.ShowAll()));
        menu.Items.Add(MenuItem(Strings.Tray_HideAll, () => _windowManager.HideAll()));
        menu.Items.Add(new Separator());

        _autoStartMenu = new MenuItem
        {
            Header = Strings.Settings_AutoStart,
            IsCheckable = true,
            IsChecked = _autoStartService.IsEnabled,
        };
        _autoStartMenu.Click += (_, _) => _autoStartService.SetEnabled(_autoStartMenu.IsChecked);
        menu.Items.Add(_autoStartMenu);

        menu.Items.Add(MenuItem(Strings.Tray_ExportBackup, async () =>
        {
            try { await _backupService.ExportInteractiveAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Export failed"); }
        }));
        menu.Items.Add(MenuItem(Strings.Tray_ImportBackup, async () =>
        {
            try { await _backupService.ImportInteractiveAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Import failed"); }
        }));
        menu.Items.Add(MenuItem(Strings.Tray_ExportNotesAsText, async () =>
        {
            try { await _backupService.ExportNotesAsTextAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Text export failed"); }
        }));
        menu.Items.Add(BuildVaultMenu());

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Strings.Tray_CheckForUpdates, async () =>
        {
            try { await _updateService.CheckAsync(userInitiated: true); }
            catch (Exception ex) { _logger.LogError(ex, "Update check failed"); }
        }));
        menu.Items.Add(MenuItem(Strings.Tray_Exit, () => Application.Current.Shutdown()));

        menu.Opened += (_, _) =>
        {
            if (_autoStartMenu is not null)
            {
                _autoStartMenu.IsChecked = _autoStartService.IsEnabled;
            }
            RefreshVaultMenu();
        };

        return menu;
    }

    public void ShowNotification(string title, string message)
    {
        try { _icon?.ShowNotification(title, message, NotificationIcon.Info); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to show tray notification"); }
    }

    private async void OpenMarkdownFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Utils.LinkedFile.OpenDialogFilter,
            Title = Strings.Tray_OpenMarkdownDialogTitle,
        };
        if (DialogOwner.Show(dlg) != true) return;
        try { await _windowManager.OpenFileAsync(dlg.FileName); }
        catch (Exception ex) { _logger.LogError(ex, "Open markdown file failed"); }
    }

    private MenuItem BuildTemplateMenu()
    {
        var parent = new MenuItem { Header = Strings.Tray_NewFromTemplate };
        foreach (var template in NoteTemplates.All)
        {
            parent.Items.Add(MenuItem(template.Name(), () => _windowManager.CreateAndShowNew(template)));
        }
        return parent;
    }

    private MenuItem BuildVaultMenu()
    {
        var parent = new MenuItem { Header = Strings.Vault_Menu };
        parent.Items.Add(MenuItem(Strings.Vault_Export, async () =>
        {
            try { await _backupService.ExportVaultAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Vault export failed"); }
        }));
        parent.Items.Add(MenuItem(Strings.Vault_Import, async () =>
        {
            try { await _backupService.ImportVaultAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Vault import failed"); }
        }));

        parent.Items.Add(new Separator());
        _openFolderMenu = MenuItem(Strings.Vault_OpenInExplorer, OpenVaultFolder);
        _obsidianMenu = MenuItem(Strings.Vault_OpenInObsidian, OpenInObsidian);
        _vsCodeMenu = MenuItem(Strings.Vault_OpenInVsCode, OpenInVsCode);
        parent.Items.Add(_openFolderMenu);
        parent.Items.Add(_obsidianMenu);
        parent.Items.Add(_vsCodeMenu);

        return parent;
    }

    /// Hide (not deactivate) items that are not in vault mode or not installed — because they are reevaluated each time the menu is opened.
    /// Installation/uninstallation/repository switching is reflected without restarting the app (same pattern as _autoStartMenu).
    private void RefreshVaultMenu()
    {
        var settings = _settingsService.Current;
        var isVault = settings.StorageMode == "vault" && !string.IsNullOrWhiteSpace(settings.VaultPath);

        if (_openFolderMenu is not null)
            _openFolderMenu.Visibility = isVault ? Visibility.Visible : Visibility.Collapsed;
        if (_obsidianMenu is not null)
            _obsidianMenu.Visibility = isVault && ExternalAppLocator.IsObsidianInstalled() ? Visibility.Visible : Visibility.Collapsed;
        if (_vsCodeMenu is not null)
            _vsCodeMenu.Visibility = isVault && ExternalAppLocator.IsVsCodeAvailable() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenVaultFolder()
    {
        var path = _settingsService.Current.VaultPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(string.Format(Strings.Vault_FolderMissingFormat, path ?? string.Empty), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch (Exception ex) { _logger.LogError(ex, "Open vault folder failed"); }
    }

    private void OpenInObsidian()
    {
        try { ExternalAppLocator.OpenObsidian(_settingsService.Current.VaultPath!); }
        catch (Exception ex) { _logger.LogError(ex, "Open in Obsidian failed"); }
    }

    private void OpenInVsCode()
    {
        try { ExternalAppLocator.OpenVsCode(_settingsService.Current.VaultPath!); }
        catch (Exception ex) { _logger.LogError(ex, "Open in VS Code failed"); }
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private sealed class DelegateCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public DelegateCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
