using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using JetBrains.Annotations;
using MessageBox.Avalonia;
using MessageBox.Avalonia.DTO;
using MessageBox.Avalonia.Enums;
using PropertyChanged.SourceGenerator;
using ReactiveUI;
using Scarab.Interfaces;
using Scarab.Models;
using Scarab.Services;
using Scarab.Util;

namespace Scarab.ViewModels
{
    public partial class ModListViewModel : ViewModelBase
    {
        private readonly SortableObservableCollection<ModItem> _items;

        private readonly ISettings _settings;
        private readonly IInstaller _installer;
        private readonly IModSource _mods;
        private readonly IModDatabase _db;
        private readonly ReverseDependencySearch _reverseDependencySearch;
        
        [Notify("ProgressBarVisible")]
        private bool _pbVisible;

        [Notify("ProgressBarIndeterminate")]
        private bool _pbIndeterminate;

        [Notify("Progress")]
        private double _pbProgress;

        [Notify]
        private IEnumerable<ModItem> _selectedItems;

        [Notify]
        private string? _search;
        
        private bool _updating;

        [Notify]
        private bool _isExactSearch;

        [Notify]
        private bool _isNormalSearch = true;

        [Notify]
        private bool _isDependencySearch = false;
        
        [Notify]
        private string _dependencySearchItem;

        [Notify]
        private ModFilterState _modFilterState = ModFilterState.All;

        public IEnumerable<string> ModNames { get; }
        public ObservableCollection<TagItem> TagList { get; }
        public ReactiveCommand<ModItem, Unit> OnUpdate { get; }
        public ReactiveCommand<ModItem, Unit> OnInstall { get; }
        public ReactiveCommand<ModItem, Unit> OnEnable { get; }
        public ReactiveCommand<TagItem, Unit> OnTagSelect { get; }
        
        public ReactiveCommand<Unit, Unit> ToggleApi { get; }
        public ReactiveCommand<Unit, Unit> UpdateApi { get; }
        
        public ReactiveCommand<Unit, Unit> ChangePath { get; }
        
        public ModListViewModel(ISettings settings, IModDatabase db, IInstaller inst, IModSource mods)
        {
            _settings = settings;
            _installer = inst;
            _mods = mods;
            _db = db;

            _items = new SortableObservableCollection<ModItem>(db.Items.OrderBy(ModToOrderedTuple));

            SelectedItems = _selectedItems = _items;
            
            _reverseDependencySearch = new (_items);

            _dependencySearchItem = "";

            ModNames = _items.Where(x => x.State is not NotInModLinksState).Select(x => x.Name);

            OnInstall = ReactiveCommand.CreateFromTask<ModItem>(OnInstallAsync);
            OnUpdate = ReactiveCommand.CreateFromTask<ModItem>(OnUpdateAsync);
            OnEnable = ReactiveCommand.CreateFromTask<ModItem>(OnEnableAsync);
            OnTagSelect = ReactiveCommand.Create<TagItem>(OnTagSelectFilter);
            ToggleApi = ReactiveCommand.Create(ToggleApiCommand);
            ChangePath = ReactiveCommand.CreateFromTask(ChangePathAsync);
            UpdateApi = ReactiveCommand.CreateFromTask(UpdateApiAsync);

            HashSet<string> tagsInModlinks = new();
            foreach (var mod in _items)
            {
                if (!mod.HasTags) continue;
                foreach (var tag in mod.Tags)
                {
                    tagsInModlinks.Add(tag);
                }
            }

            TagList = new ObservableCollection<TagItem>(tagsInModlinks.Select(x => new TagItem(x, false)));
        }
        
        [UsedImplicitly]
        public void ClearSearch()
        {
            Search = "";
            DependencySearchItem = "";
        }
        
        [UsedImplicitly]
        private bool NoFilteredItems => !FilteredItems.Any();

        [UsedImplicitly]
        private IEnumerable<ModItem> FilteredItems
        {
            get
            {
                if (IsNormalSearch)
                {
                    if (string.IsNullOrEmpty(Search)) 
                        return SelectedItems;
                    
                    if (IsExactSearch)
                        return SelectedItems.Where(x => x.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
                    else 
                        return SelectedItems.Where(x => x.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                                                         x.Description.Contains(Search, StringComparison.OrdinalIgnoreCase));
                }
                if (IsDependencySearch)
                {
                    if (string.IsNullOrEmpty(DependencySearchItem))
                        return SelectedItems;
                    
                    // this isnt user input so we can do normal comparison
                    var mod = _items.First(x => x.Name == DependencySearchItem);
                    return SelectedItems
                        .Intersect(_reverseDependencySearch.GetAllDependentAndIntegratedMods(mod));
                }

                return SelectedItems;
            }
        }

        public string ApiButtonText   => _mods.ApiInstall is InstalledState { Enabled: var enabled } 
            ? (
                enabled ? Resources.MLVM_ApiButtonText_DisableAPI 
                        : Resources.MLVM_ApiButtonText_EnableAPI 
            )
            : Resources.MLVM_ApiButtonText_ToggleAPI;
        
        public bool ApiOutOfDate => _mods.ApiInstall is InstalledState { Version: var v } && v.Major < _db.Api.Version;

        public bool EnableApiButton => _mods.ApiInstall switch
        {
            NotInstalledState => false,
            // Disabling, so we're putting back the vanilla assembly
            InstalledState { Enabled: true } => File.Exists(Path.Combine(_settings.ManagedFolder, Installer.Vanilla)),
            // Enabling, so take the modded one.
            InstalledState { Enabled: false } => File.Exists(Path.Combine(_settings.ManagedFolder, Installer.Modded)),
            // Unreachable
            _ => throw new InvalidOperationException()
        };
        
        public bool CanUpdateAll => _items.Any(x => x.State is InstalledState { Updated: false }) && !_updating;
        public bool CanUninstallAll => _items.Any(x => x.State is InstalledState or NotInModLinksState);
        public bool CanDisableAll => _items.Any(x => x.State is InstalledState {Enabled: true} or NotInModLinksState {Enabled: true});
        public bool CanEnableAll => _items.Any(x => x.State is InstalledState {Enabled: false} or NotInModLinksState {Enabled: false});

        // Needed for source generator to find it.
        private void RaisePropertyChanged(string name) => IReactiveObjectExtensions.RaisePropertyChanged(this, name);
        private void RaisePropertyChanging(string name) => IReactiveObjectExtensions.RaisePropertyChanging(this, name);

        private async void ToggleApiCommand()
        {
            await _installer.ToggleApi();
            
            RaisePropertyChanged(nameof(ApiButtonText));
            RaisePropertyChanged(nameof(EnableApiButton));
        }
        
        private async Task ChangePathAsync()
        {
            string? path = await PathUtil.SelectPathFallible();

            if (path is null)
                return;

            _settings.ManagedFolder = path;
            _settings.Save();

            await _mods.Reset();

            await MessageBoxManager.GetMessageBoxStandardWindow(Resources.MLVM_ChangePathAsync_Msgbox_Title, 
                Resources.MLVM_ChangePathAsync_Msgbox_Text).Show();
            
            // Shutting down is easier than re-doing the source and all the items.
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }

        public void OpenModsDirectory()
        {
            var modsFolder = Path.Combine(_settings.ManagedFolder, "Mods");

            // Create the directory if it doesn't exist,
            // so we don't open a non-existent folder.
            Directory.CreateDirectory(modsFolder);
            
            Process.Start(new ProcessStartInfo(modsFolder) {
                    UseShellExecute = true
            });
        }

        public static void Donate() => Process.Start(new ProcessStartInfo("https://paypal.me/ybham") { UseShellExecute = true });

        [UsedImplicitly]
        public void SelectMods(ModFilterState modFilterState)
        {
            ModFilterState = modFilterState;
            FilterMods();
        }
        
        private void FilterMods()
        {
            SelectedItems = _modFilterState switch
            {
                ModFilterState.All => _items,
                ModFilterState.Installed => _items.Where(x => x.Installed),
                ModFilterState.Enabled => _items.Where(x => x.State is InstalledState { Enabled: true } or NotInModLinksState { Enabled: true }),
                ModFilterState.OutOfDate => _items.Where(x => x.State is InstalledState { Updated: false }),
                _ => throw new InvalidOperationException("Invalid mod filter state")
            };
            
            var selectedTags = TagList
                .Where(x => x.IsSelected)
                .Select(x => x.TagName)
                .ToList();

            if (selectedTags.Count > 0)
            {
                SelectedItems = SelectedItems
                    .Where(x => x.HasTags && 
                                x.Tags.Any(tagsDefined => selectedTags
                                    .Any(tagsSelected => tagsSelected == tagsDefined)));
            }
            
            RaisePropertyChanged(nameof(FilteredItems));
        }

        public async Task UpdateUnupdated()
        {
            _updating = false;
            
            RaisePropertyChanged(nameof(CanUpdateAll));
            
            var outOfDate = _items.Where(x => x.State is InstalledState { Updated: false }).ToList();

            foreach (ModItem mod in outOfDate)
            {
                // Mods can get updated as dependencies of others while doing this
                if (mod.State is not InstalledState { Updated: false })
                    continue;
                
                await OnUpdateAsync(mod);
            }
        }

        [UsedImplicitly]
        private async Task UninstallAll()
        {
            var toUninstall = _items.Where(x => x.State is InstalledState or NotInModLinksState).ToList();
            foreach (ModItem mod in toUninstall)
            {
                if (mod.State is not (InstalledState or NotInModLinksState))
                    continue;
                
                await InternalUpdateInstallAsync(mod, mod.OnInstall);
            }
        }

        public void DisableAllInstalled()
        {
            var toDisable = _items.Where(x => x.State is InstalledState {Enabled:true} or NotInModLinksState{Enabled:true}).ToList();

            foreach (ModItem mod in toDisable)
            {
                if (mod.State is not (InstalledState {Enabled:true} or NotInModLinksState {Enabled:true}))
                    continue;

                _installer.Toggle(mod);
            }

            RaisePropertyChanged(nameof(CanDisableAll));
            RaisePropertyChanged(nameof(CanEnableAll));
        }
        
        public async Task ForceUpdateAll()
        {
            var toUpdate = _items.Where(x => x.State is InstalledState).ToList();
            
            foreach (ModItem mod in toUpdate)
            {
                var state = (InstalledState) mod.State;
                mod.State = new InstalledState(state.Enabled, new Version(0,0,0,0), false);
                await _mods.RecordInstalledState(mod);
                mod.CallOnPropertyChanged(nameof(mod.UpdateAvailable));
                mod.CallOnPropertyChanged(nameof(mod.VersionText));
            }
            
            RaisePropertyChanged(nameof(FilteredItems));
            RaisePropertyChanged(nameof(SelectedItems));
            await UpdateUnupdated();
        }

        private async Task OnEnableAsync(ModItem item)
        {
            try
            {
                var dependents = _reverseDependencySearch.GetAllEnabledDependents(item).ToList();

                if (!item.EnabledIsChecked ||
                    dependents.Count == 0 ||
                    await DisplayHasDependentsWarning(item.Name, dependents))
                {
                    _installer.Toggle(item);
                }

                // to reset the visuals of the toggle to the correct value
                item.CallOnPropertyChanged(nameof(item.EnabledIsChecked));
                RaisePropertyChanged(nameof(CanDisableAll));
                RaisePropertyChanged(nameof(CanEnableAll));
                FilterMods();
                
            }
            catch (Exception e)
            {
                await DisplayGenericError("toggling", item.Name, e);
            }
        }

        private async Task UpdateApiAsync()
        {
            try
            {
                await _installer.InstallApi();
            }
            catch (HashMismatchException e)
            {
                await DisplayHashMismatch(e);
            }
            catch (Exception e)
            {
                await DisplayGenericError("updating", name: "the API", e);
            }

            RaisePropertyChanged(nameof(ApiOutOfDate));
            RaisePropertyChanged(nameof(ApiButtonText));
            RaisePropertyChanged(nameof(EnableApiButton));
        }

        private async Task InternalUpdateInstallAsync(ModItem item, Func<IInstaller, Action<ModProgressArgs>, Task> f)
        {
            static bool IsHollowKnight(Process p) => (
                   p.ProcessName.StartsWith("hollow_knight")
                || p.ProcessName.StartsWith("Hollow Knight")
            );
            
            if (Process.GetProcesses().FirstOrDefault(IsHollowKnight) is { } proc)
            {
                var res = await MessageBoxManager.GetMessageBoxStandardWindow(new MessageBoxStandardParams {
                    ContentTitle = Resources.MLVM_InternalUpdateInstallAsync_Msgbox_W_Title,
                    ContentMessage = Resources.MLVM_InternalUpdateInstallAsync_Msgbox_W_Text,
                    ButtonDefinitions = ButtonEnum.YesNo,
                    MinHeight = 200,
                    SizeToContent = SizeToContent.WidthAndHeight,
                }).Show();

                if (res == ButtonResult.Yes)
                    proc.Kill();
            }
            
            try
            {
                // ensure no duplicate mods
                if (item.State is NotInstalledState)
                {
                    foreach (var similarItem in _items.Where(x => x.Name == item.Name && x.State is InstalledState or NotInModLinksState).ToList())
                    {
                        await _installer.Uninstall(similarItem);
                    }
                }
                await f
                (
                    _installer,
                    progress =>
                    {
                        ProgressBarVisible = !progress.Completed;

                        if (progress.Download?.PercentComplete is not { } percent)
                        {
                            ProgressBarIndeterminate = true;
                            return;
                        }

                        ProgressBarIndeterminate = false;
                        Progress = percent;
                    }
                );
            }
            catch (HashMismatchException e)
            {
                Trace.WriteLine($"Mod {item.Name} had a hash mismatch! Expected: {e.Expected}, got {e.Actual}");
                await DisplayHashMismatch(e);
            }
            catch (HttpRequestException e)
            {
                await DisplayNetworkError(item.Name, e);
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Failed to install mod {item.Name}. State = {item.State}, Link = {item.Link}");
                await DisplayGenericError("installing or uninstalling", item.Name, e);
            }

            // Even if we threw, stop the progress bar.
            ProgressBarVisible = false;

            RaisePropertyChanged(nameof(ApiButtonText));
            RaisePropertyChanged(nameof(EnableApiButton));

            static int Comparer(ModItem x, ModItem y) => ModToOrderedTuple(x).CompareTo(ModToOrderedTuple(y));
            
            var removeList = _items.Where(x => x.State is NotInModLinksState { Installed: false }).ToList();
            foreach (var _item in removeList)
            {
                _items.Remove(_item);
                SelectedItems = SelectedItems.Where(x => x != _item);
            }
            _items.SortBy(Comparer);

            RaisePropertyChanged(nameof(CanUninstallAll));
            RaisePropertyChanged(nameof(CanDisableAll));
            RaisePropertyChanged(nameof(CanEnableAll));
            
            FilterMods();
            
            _items.SortBy(Comparer);
        }
        private async Task InternalInstallWithConfirmationAsync(ModItem item, Func<IInstaller, Action<ModProgressArgs>, Task> f)
        {
            var dependents = _reverseDependencySearch.GetAllEnabledDependents(item).ToList();

            if (!item.Installed ||
                dependents.Count == 0 ||
                await DisplayHasDependentsWarning(item.Name, dependents))
            {
                await InternalUpdateInstallAsync(item, f);
            }
        }

        private async Task OnUpdateAsync(ModItem item) => await InternalUpdateInstallAsync(item, item.OnUpdate);

        private async Task OnInstallAsync(ModItem item) => await InternalInstallWithConfirmationAsync(item, item.OnInstall);
        
        private void OnTagSelectFilter(TagItem _) => FilterMods();
        

        private static async Task DisplayHashMismatch(HashMismatchException e)
        {
            await MessageBoxManager.GetMessageBoxStandardWindow
            (
                title: Resources.MLVM_DisplayHashMismatch_Msgbox_Title,
                text: string.Format(Resources.MLVM_DisplayHashMismatch_Msgbox_Text, e.Name, e.Actual, e.Expected),
                icon: Icon.Error
            ).Show();
        }

        private static async Task DisplayGenericError(string action, string name, Exception e)
        {
            Trace.TraceError(e.ToString());

            await MessageBoxManager.GetMessageBoxStandardWindow
            (
                title: "Error!",
                text: $"An exception occured while {action} {name}.",
                icon: Icon.Error
            ).Show();
        }

        private static async Task DisplayNetworkError(string name, HttpRequestException e)
        {
            Trace.WriteLine($"Failed to download {name}, {e}");

            await MessageBoxManager.GetMessageBoxStandardWindow
            (
                title: Resources.MLVM_DisplayNetworkError_Msgbox_Title,
                text: string.Format(Resources.MLVM_DisplayNetworkError_Msgbox_Text, name),
                icon: Icon.Error
            ).Show();
        }
        
        // asks user for confirmation on whether or not they want to uninstall/disable mod.
        // returns whether or not user presses yes on the message box
        private static async Task<bool> DisplayHasDependentsWarning(string modName, IEnumerable<ModItem> dependents)
        {
            var dependentsString = string.Join(", ", dependents.Select(x => x.Name));
            var result = await MessageBoxManager.GetMessageBoxStandardWindow
            (
                title: "Warning! This mod is required for other mods to function!",
                text: $"{modName} is required for {dependentsString} to function properly. Do you still want to continue?",
                icon: Icon.Stop,
                @enum: ButtonEnum.YesNo
            ).Show();

            // return whether or not yes was clicked. Also don't remove mod when box is closed with the x
            return result.HasFlag(ButtonResult.Yes) && !result.HasFlag(ButtonResult.None);
        }

        private static (int priority, string name) ModToOrderedTuple(ModItem m) =>
        (
            m.State is InstalledState { Updated : false } ? -1 : 1,
            m.Name
        );
    }
}
