using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stickmark.Resources;
using Stickmark.Services;
using Stickmark.Utils;

namespace Stickmark.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IHotkeyService _hotkeys;
    private readonly IAutoStartService _autoStart;

    /// Language selection combobox items: value (code stored in settings) and display label.
    public sealed record LanguageOption(string Value, string Label);

    /// Storage selection combobox items: value (code stored in settings) and display label.
    public sealed record StorageOption(string Value, string Label);

    [ObservableProperty]
    private bool _autoStartWithWindows;

    [ObservableProperty]
    private bool _globalHotkeysEnabled;

    [ObservableProperty]
    private string _newNoteHotkey;

    [ObservableProperty]
    private string _notesListHotkey;

    [ObservableProperty]
    private bool _autoCheckForUpdates;

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultMode))]
    private string _selectedStorageMode;

    [ObservableProperty]
    private string? _vaultPath;

    [ObservableProperty]
    private string? _validationError;

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
    [
        new LanguageOption("system", Strings.Settings_LanguageSystem),
        new LanguageOption("en", "English"),
    ];

    public IReadOnlyList<StorageOption> StorageOptions { get; } =
    [
        new StorageOption("litedb", Strings.Settings_StorageLiteDb),
        new StorageOption("vault", Strings.Settings_StorageVault),
    ];

    /// Whether the storage selection is in vault mode — bound to whether the folder selection UI is visible.
    public bool IsVaultMode => SelectedStorageMode == "vault";

    /// Raised after a successful save so the hosting window can close itself.
    public event EventHandler? Saved;

    public SettingsViewModel(ISettingsService settings, IHotkeyService hotkeys, IAutoStartService autoStart)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _autoStart = autoStart;

        var current = settings.Current;
        // Auto-start's source of truth is the registry, not the settings file.
        _autoStartWithWindows = autoStart.IsEnabled;
        _globalHotkeysEnabled = current.GlobalHotkeysEnabled;
        _newNoteHotkey = current.NewNoteHotkey;
        _notesListHotkey = current.NotesListHotkey;
        _autoCheckForUpdates = current.AutoCheckForUpdates;
        _selectedLanguage = current.Language;
        _selectedStorageMode = current.StorageMode;
        _vaultPath = current.VaultPath;
    }

    [RelayCommand]
    private void BrowseVault()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.Settings_VaultFolderDialogTitle };
        if (dialog.ShowDialog() == true)
        {
            VaultPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (GlobalHotkeysEnabled)
        {
            if (!HotkeyGesture.TryParse(NewNoteHotkey, out _, out _))
            {
                ValidationError = Strings.Settings_HotkeyInvalidNewNote;
                return;
            }
            if (!HotkeyGesture.TryParse(NotesListHotkey, out _, out _))
            {
                ValidationError = Strings.Settings_HotkeyInvalidList;
                return;
            }
        }
        if (SelectedStorageMode == "vault" && string.IsNullOrWhiteSpace(VaultPath))
        {
            ValidationError = Strings.Settings_VaultPathRequired;
            return;
        }
        ValidationError = null;

        var current = _settings.Current;
        current.GlobalHotkeysEnabled = GlobalHotkeysEnabled;
        current.NewNoteHotkey = NewNoteHotkey;
        current.NotesListHotkey = NotesListHotkey;
        current.AutoStartWithWindows = AutoStartWithWindows;
        current.AutoCheckForUpdates = AutoCheckForUpdates;
        current.Language = SelectedLanguage;
        current.StorageMode = SelectedStorageMode;
        current.VaultPath = VaultPath;
        await _settings.SaveAsync().ConfigureAwait(true);

        _autoStart.SetEnabled(AutoStartWithWindows);
        _hotkeys.Apply(GlobalHotkeysEnabled, NewNoteHotkey, NotesListHotkey);

        Saved?.Invoke(this, EventArgs.Empty);
    }
}
