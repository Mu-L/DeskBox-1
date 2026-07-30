using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    private const string LooseItemOrderKeyPrefix = "Item:";
    private const string ManualStackKeyPrefix = "Manual:";
    private readonly ObservableCollection<WidgetItem> _stackDisplayItems = [];
    private readonly Dictionary<string, WidgetStackItem> _stackItems = [];
    private bool _fileStacksEnabled;
    private string _fileStackGroupBy = SettingsService.FileStackGroupByKind;
    private int _fileStackThreshold = SettingsService.DefaultFileStackThreshold;
    private string _fileStackOrderBy = SettingsService.FileStackOrderByWidget;
    private string? _expandedStackKey;
    private bool _stackRebuildQueued;
    private DispatcherQueueTimer? _stackDateBoundaryTimer;
    private HashSet<string> _disabledStacks = new(StringComparer.Ordinal);
    private Dictionary<string, string> _stackNameOverrides = new(StringComparer.Ordinal);
    private List<string> _stackOrder = [];
    private Dictionary<string, List<string>> _stackMemberOverrides =
        new(StringComparer.Ordinal);

    public IEnumerable<WidgetItem> VisibleItems => FileStacksEnabled
        ? _stackDisplayItems
        : Items;

    public bool FileStacksEnabled
    {
        get => _fileStacksEnabled;
        private set => SetProperty(ref _fileStacksEnabled, value);
    }

    public string FileStackGroupBy
    {
        get => _fileStackGroupBy;
        private set => SetProperty(ref _fileStackGroupBy, value);
    }

    public int FileStackThreshold
    {
        get => _fileStackThreshold;
        private set => SetProperty(ref _fileStackThreshold, value);
    }

    public string FileStackOrderBy
    {
        get => _fileStackOrderBy;
        private set => SetProperty(ref _fileStackOrderBy, value);
    }

    public bool IsStackDisabled(string stackKey) => _disabledStacks.Contains(stackKey);

    public bool HasDisabledStacks => _disabledStacks.Count > 0;

    public bool FileStacksFollowGlobalDefaults =>
        WidgetFileStackSettings.FollowsGlobalDefaults(Config);

    public bool FileStacksEnabledFollowsGlobal =>
        WidgetFileStackSettings.GetEnabledOverride(Config) is null;

    public bool FileStackGroupByFollowsGlobal =>
        WidgetFileStackSettings.GetGroupByOverride(Config) is null;

    public bool FileStackThresholdFollowsGlobal =>
        WidgetFileStackSettings.GetThresholdOverride(Config) is null;

    public bool FileStackOrderByFollowsGlobal =>
        WidgetFileStackSettings.GetOrderByOverride(Config) is null;

    public void ToggleStack(WidgetStackItem stack)
    {
        SetStackExpanded(stack, !stack.IsExpanded);
    }

    public void SetStackExpanded(WidgetStackItem stack, bool expanded)
    {
        _expandedStackKey = expanded ? stack.StackKey : null;
        RebuildStackDisplayItems();
    }

    public bool CollapseExpandedStack()
    {
        if (string.IsNullOrEmpty(_expandedStackKey))
        {
            return false;
        }

        _expandedStackKey = null;
        RebuildStackDisplayItems();
        return true;
    }

    /// <summary>
    /// Prepares an expanded automatic-stack member for direct manipulation.
    /// A manual drag becomes the user's ordering preference, so both the
    /// widget and the stack use their persisted widget order from this point.
    /// </summary>
    public bool PrepareVisibleItemReorder(WidgetItem item)
    {
        if (!FileStacksEnabled)
        {
            return false;
        }

        if (item.IsStackChild)
        {
            return PrepareStackMemberReorder(item);
        }

        if (item is WidgetStackItem ||
            !_stackDisplayItems.Any(candidate =>
                ReferenceEquals(candidate, item)))
        {
            return false;
        }

        EnsureStackManualOrder();
        return true;
    }

    public bool PrepareStackMemberReorder(WidgetItem item)
    {
        if (!FileStacksEnabled ||
            !item.IsStackChild ||
            string.IsNullOrWhiteSpace(_expandedStackKey))
        {
            return false;
        }

        EnsureStackManualOrder();
        return item.IsStackChild;
    }

    private void EnsureStackManualOrder()
    {
        if (!string.Equals(
                FileStackOrderBy,
                SettingsService.FileStackOrderByWidget,
                StringComparison.Ordinal))
        {
            SetFileStackOrderByOverride(
                SettingsService.FileStackOrderByWidget);
        }

        if (Config.SortMode != WidgetSortMode.Manual)
        {
            SetSortMode(WidgetSortMode.Manual);
        }
    }

    /// <summary>
    /// Reorders a member within the currently expanded automatic stack. The
    /// target is expressed in VisibleItems coordinates so both window hosts
    /// can share the exact same stack-boundary and persistence behavior.
    /// </summary>
    public bool MoveExpandedStackMemberForReorder(
        WidgetItem item,
        int visibleTargetIndex)
    {
        if (!PrepareStackMemberReorder(item) ||
            _expandedStackKey is null ||
            !_stackItems.TryGetValue(
                _expandedStackKey,
                out WidgetStackItem? stack))
        {
            return false;
        }

        int stackIndex = IndexOfReference(
            _stackDisplayItems,
            stack,
            0);
        int currentVisibleIndex = IndexOfReference(
            _stackDisplayItems,
            item,
            0);
        if (stackIndex < 0 || currentVisibleIndex < 0)
        {
            return false;
        }

        int firstMemberIndex = stackIndex + 1;
        int lastMemberIndex =
            firstMemberIndex + stack.Members.Count - 1;
        int targetVisibleIndex = Math.Clamp(
            visibleTargetIndex,
            firstMemberIndex,
            lastMemberIndex);
        if (targetVisibleIndex == currentVisibleIndex)
        {
            return false;
        }

        int targetMemberIndex =
            targetVisibleIndex - firstMemberIndex;
        WidgetItem targetMember = stack.Members[targetMemberIndex];
        int currentItemIndex = Items.IndexOf(item);
        int targetItemIndex = Items.IndexOf(targetMember);
        if (currentItemIndex < 0 ||
            targetItemIndex < 0 ||
            currentItemIndex == targetItemIndex)
        {
            return false;
        }

        Items.Move(currentItemIndex, targetItemIndex);
        // CollectionChanged also queues a rebuild. Rebuilding synchronously
        // keeps the insertion feedback attached to the pointer during drag.
        RebuildStackDisplayItems();
        return true;
    }

    public bool MoveVisibleItemForReorder(
        WidgetItem item,
        int visibleInsertionIndex)
    {
        if (!FileStacksEnabled)
        {
            return false;
        }

        if (item.IsStackChild)
        {
            int currentVisibleIndex = IndexOfReference(
                _stackDisplayItems,
                item,
                0);
            if (currentVisibleIndex < 0)
            {
                return false;
            }

            int targetVisibleIndex = visibleInsertionIndex;
            if (targetVisibleIndex > currentVisibleIndex)
            {
                targetVisibleIndex--;
            }

            return MoveExpandedStackMemberForReorder(
                item,
                targetVisibleIndex);
        }

        return PrepareVisibleItemReorder(item) &&
            MoveDisplayUnitForReorder(
                GetLooseItemOrderKey(item),
                visibleInsertionIndex);
    }

    internal void StabilizeStackDisplay()
    {
        RebuildStackDisplayItems();
        foreach (var stack in _stackItems.Values)
        {
            stack.RefreshPresentationState();
        }
    }

    public void SetFileStacksEnabledOverride(bool? enabled)
    {
        WidgetFileStackSettings.SetEnabledOverride(Config, enabled);
        PersistStackOverrides();
    }

    public void SetFileStackGroupByOverride(string? groupBy)
    {
        WidgetFileStackSettings.SetGroupByOverride(Config, groupBy);
        PersistStackOverrides();
    }

    public void SetFileStackThresholdOverride(int? threshold)
    {
        WidgetFileStackSettings.SetThresholdOverride(Config, threshold);
        PersistStackOverrides();
    }

    public void SetFileStackOrderByOverride(string? orderBy)
    {
        WidgetFileStackSettings.SetOrderByOverride(Config, orderBy);
        PersistStackOverrides();
    }

    public void ClearFileStackOverrides()
    {
        WidgetFileStackSettings.ClearOverrides(Config);
        PersistStackOverrides();
    }

    public void SetStackDisabled(string stackKey, bool disabled)
    {
        if (disabled)
        {
            _disabledStacks.Add(stackKey);
        }
        else
        {
            _disabledStacks.Remove(stackKey);
        }
        WidgetFileStackSettings.SetDisabledStacks(Config, _disabledStacks);
        PersistStackOverrides();
        QueueStackDisplayRebuild();
    }

    public void SetStackNameOverride(string stackKey, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _stackNameOverrides.Remove(stackKey);
        }
        else
        {
            _stackNameOverrides[stackKey] = name.Trim();
        }
        WidgetFileStackSettings.SetStackNameOverrides(Config, _stackNameOverrides);
        PersistStackOverrides();
        QueueStackDisplayRebuild();
    }

    public void SetStackOrder(List<string> order)
    {
        _stackOrder = order.ToList();
        WidgetFileStackSettings.SetStackOrder(Config, _stackOrder);
        PersistStackOverrides();
        QueueStackDisplayRebuild();
    }

    public bool CreateManualStack(
        IEnumerable<WidgetItem> selectedItems)
    {
        if (!FileStacksEnabled)
        {
            return false;
        }

        List<WidgetItem> members =
            NormalizeStackMembers(selectedItems);
        if (members.Count < 2)
        {
            return false;
        }

        EnsureStackManualOrder();
        List<string> currentOrder =
            GetCurrentDisplayUnitOrder();
        int insertionIndex = ResolveManualStackInsertionIndex(
            currentOrder,
            members);
        HashSet<string> memberPaths = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveMemberOverrides(memberPaths);

        string stackKey =
            $"{ManualStackKeyPrefix}{Guid.NewGuid():N}";
        _stackMemberOverrides[stackKey] = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToList();
        currentOrder.RemoveAll(key =>
            memberPaths.Any(path =>
                string.Equals(
                    key,
                    LooseItemOrderKeyPrefix +
                        path.ToUpperInvariant(),
                    StringComparison.Ordinal)));
        insertionIndex = Math.Clamp(
            insertionIndex,
            0,
            currentOrder.Count);
        currentOrder.Insert(insertionIndex, stackKey);
        _stackOrder = currentOrder
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _expandedStackKey = null;
        PersistStackCustomizations();
        return true;
    }

    public bool AddItemsToStack(
        string stackKey,
        IEnumerable<WidgetItem> draggedItems)
    {
        if (!FileStacksEnabled ||
            string.IsNullOrWhiteSpace(stackKey) ||
            !_stackItems.TryGetValue(
                stackKey,
                out WidgetStackItem? targetStack))
        {
            return false;
        }

        HashSet<string> targetPaths = targetStack.Members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<WidgetItem> members = NormalizeStackMembers(
                draggedItems)
            .Where(item => !targetPaths.Contains(
                NormalizeStackMemberPath(item.Path)))
            .ToList();
        if (members.Count == 0)
        {
            return false;
        }

        EnsureStackManualOrder();
        HashSet<string> memberPaths = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveMemberOverrides(memberPaths);
        if (!_stackMemberOverrides.TryGetValue(
                stackKey,
                out List<string>? forcedMembers))
        {
            forcedMembers = [];
            _stackMemberOverrides[stackKey] = forcedMembers;
        }

        foreach (string path in memberPaths)
        {
            if (!forcedMembers.Contains(
                    path,
                    StringComparer.OrdinalIgnoreCase))
            {
                forcedMembers.Add(path);
            }
        }

        _stackOrder = GetCurrentDisplayUnitOrder()
            .Where(key => !memberPaths.Any(path =>
                string.Equals(
                    key,
                    LooseItemOrderKeyPrefix +
                        path.ToUpperInvariant(),
                    StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        PersistStackCustomizations();
        return true;
    }

    public void MoveStackUp(string stackKey)
    {
        var order = GetOrCreateOrder();
        int idx = order.IndexOf(stackKey);
        if (idx <= 0) return;
        (order[idx - 1], order[idx]) = (order[idx], order[idx - 1]);
        SetStackOrder(order);
    }

    public void MoveStackDown(string stackKey)
    {
        var order = GetOrCreateOrder();
        int idx = order.IndexOf(stackKey);
        if (idx < 0 || idx >= order.Count - 1) return;
        (order[idx + 1], order[idx]) = (order[idx], order[idx + 1]);
        SetStackOrder(order);
    }

    public bool MoveStackForReorder(
        string stackKey,
        int visibleInsertionIndex)
    {
        if (!FileStacksEnabled ||
            string.IsNullOrWhiteSpace(stackKey))
        {
            return false;
        }

        EnsureStackManualOrder();
        return MoveDisplayUnitForReorder(
            stackKey,
            visibleInsertionIndex);
    }

    private bool MoveDisplayUnitForReorder(
        string orderKey,
        int visibleInsertionIndex)
    {
        List<string> currentOrder = GetCurrentDisplayUnitOrder();
        if (!currentOrder.Contains(orderKey, StringComparer.Ordinal))
        {
            return false;
        }

        int desiredIndex = 0;
        int cappedIndex = Math.Clamp(
            visibleInsertionIndex,
            0,
            _stackDisplayItems.Count);
        for (int index = 0; index < cappedIndex; index++)
        {
            WidgetItem candidate = _stackDisplayItems[index];
            if (!IsTopLevelDisplayUnit(candidate))
            {
                continue;
            }

            string candidateKey = GetDisplayUnitOrderKey(candidate);
            if (!string.Equals(
                    candidateKey,
                    orderKey,
                    StringComparison.Ordinal))
            {
                desiredIndex++;
            }
        }

        List<string> reordered = currentOrder
            .Where(key => !string.Equals(
                key,
                orderKey,
                StringComparison.Ordinal))
            .ToList();
        desiredIndex = Math.Clamp(
            desiredIndex,
            0,
            reordered.Count);
        reordered.Insert(desiredIndex, orderKey);
        if (currentOrder.SequenceEqual(
                reordered,
                StringComparer.Ordinal))
        {
            return false;
        }

        _stackOrder = reordered;
        PersistStackDisplayOrder();
        return true;
    }

    private List<string> GetOrCreateOrder()
    {
        if (_stackOrder.Count > 0) return _stackOrder;
        _stackOrder = GetCurrentDisplayUnitOrder();
        return _stackOrder;
    }

    private List<string> GetCurrentDisplayUnitOrder()
    {
        return _stackDisplayItems
            .Where(IsTopLevelDisplayUnit)
            .Select(GetDisplayUnitOrderKey)
            .ToList();
    }

    private static bool IsTopLevelDisplayUnit(
        WidgetItem item) =>
        item is WidgetStackItem || !item.IsStackChild;

    private static string GetDisplayUnitOrderKey(
        WidgetItem item) =>
        item is WidgetStackItem stack
            ? stack.StackKey
            : GetLooseItemOrderKey(item);

    private static string GetLooseItemOrderKey(WidgetItem item)
    {
        string path;
        try
        {
            path = Path.GetFullPath(item.Path);
        }
        catch (Exception)
        {
            path = item.Path;
        }

        return LooseItemOrderKeyPrefix + path.ToUpperInvariant();
    }

    private void PersistStackDisplayOrder()
    {
        WidgetFileStackSettings.SetStackOrder(
            Config,
            _stackOrder);
        _settingsService.UpdateWidget(
            Config,
            notifySubscribers: false);
        _settingsService.SaveDebounced(
            notifySubscribers: false);
        RebuildStackDisplayItems();
    }

    private void PersistStackCustomizations()
    {
        WidgetFileStackSettings.SetStackMemberOverrides(
            Config,
            _stackMemberOverrides);
        WidgetFileStackSettings.SetStackOrder(
            Config,
            _stackOrder);
        _settingsService.UpdateWidget(
            Config,
            notifySubscribers: false);
        _settingsService.SaveDebounced(
            notifySubscribers: false);
        RebuildStackDisplayItems();
    }

    private void PersistStackOverrides()
    {
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        ApplyStackSettings();
        OnPropertyChanged(nameof(FileStacksFollowGlobalDefaults));
        OnPropertyChanged(nameof(FileStacksEnabledFollowsGlobal));
        OnPropertyChanged(nameof(FileStackGroupByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackThresholdFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOrderByFollowsGlobal));
    }

    private void InitializeStacks()
    {
        _fileStacksEnabled = WidgetFileStackSettings.ResolveEnabled(
            Config,
            _settingsService.Settings.FileStacksEnabled);
        _fileStackGroupBy = WidgetFileStackSettings.ResolveGroupBy(
            Config,
            _settingsService.Settings.FileStackGroupBy);
        _fileStackThreshold = WidgetFileStackSettings.ResolveThreshold(
            Config,
            _settingsService.Settings.FileStackThreshold);
        _fileStackOrderBy = WidgetFileStackSettings.ResolveOrderBy(
            Config,
            _settingsService.Settings.FileStackOrderBy);
        _disabledStacks = WidgetFileStackSettings.GetDisabledStacks(Config);
        _stackNameOverrides = WidgetFileStackSettings.GetStackNameOverrides(Config);
        _stackOrder = WidgetFileStackSettings.GetStackOrder(Config);
        _stackMemberOverrides =
            WidgetFileStackSettings.GetStackMemberOverrides(Config);
        Items.CollectionChanged += StackSourceItems_CollectionChanged;
        ScheduleStackDateBoundaryRefresh();
        QueueStackDisplayRebuild();
    }

    private void CleanupStacks()
    {
        Items.CollectionChanged -= StackSourceItems_CollectionChanged;
        if (_stackDateBoundaryTimer is not null)
        {
            _stackDateBoundaryTimer.Stop();
            _stackDateBoundaryTimer.Tick -= StackDateBoundaryTimer_Tick;
            _stackDateBoundaryTimer = null;
        }
    }

    private void StackSourceItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueStackDisplayRebuild();
    }

    private void QueueStackDisplayRebuild()
    {
        if (_stackRebuildQueued)
        {
            return;
        }

        _stackRebuildQueued = true;
        _dispatcherQueue.TryEnqueue(() =>
        {
            _stackRebuildQueued = false;
            RebuildStackDisplayItems();
        });
    }

    private void ApplyStackSettings()
    {
        bool enabled = WidgetFileStackSettings.ResolveEnabled(
            Config,
            _settingsService.Settings.FileStacksEnabled);
        string groupBy = WidgetFileStackSettings.ResolveGroupBy(
            Config,
            _settingsService.Settings.FileStackGroupBy);
        int threshold = WidgetFileStackSettings.ResolveThreshold(
            Config,
            _settingsService.Settings.FileStackThreshold);
        string orderBy = WidgetFileStackSettings.ResolveOrderBy(
            Config,
            _settingsService.Settings.FileStackOrderBy);
        var disabledStacks = WidgetFileStackSettings.GetDisabledStacks(Config);
        var nameOverrides = WidgetFileStackSettings.GetStackNameOverrides(Config);
        var stackOrder = WidgetFileStackSettings.GetStackOrder(Config);
        var stackMemberOverrides =
            WidgetFileStackSettings.GetStackMemberOverrides(Config);
        bool sourceChanged = FileStacksEnabled != enabled;
        FileStacksEnabled = enabled;
        FileStackGroupBy = groupBy;
        FileStackThreshold = threshold;
        FileStackOrderBy = orderBy;
        _disabledStacks = disabledStacks;
        _stackNameOverrides = nameOverrides;
        _stackOrder = stackOrder;
        _stackMemberOverrides = stackMemberOverrides;
        if (!enabled)
        {
            _expandedStackKey = null;
        }

        RebuildStackDisplayItems();
        ScheduleStackDateBoundaryRefresh();
        if (sourceChanged)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }

        OnPropertyChanged(nameof(FileStacksFollowGlobalDefaults));
        OnPropertyChanged(nameof(FileStacksEnabledFollowsGlobal));
        OnPropertyChanged(nameof(FileStackGroupByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackThresholdFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOrderByFollowsGlobal));
        OnPropertyChanged(nameof(HasDisabledStacks));
    }

    private void RebuildStackDisplayItems()
    {
        foreach (var item in Items)
        {
            item.IsStackChild = false;
        }

        if (!FileStacksEnabled)
        {
            _stackDisplayItems.Clear();
            return;
        }

        IReadOnlyList<WidgetStackGroup> groups =
            ApplyStackMemberOverrides(
                WidgetStackGroupingService.Group(
            Items,
            FileStackGroupBy,
            orderBy: FileStackOrderBy,
            customRules: _settingsService.Settings.FileStackCustomRules,
            unmatchedBehavior:
                _settingsService.Settings.FileStackUnmatchedBehavior));
        if (_expandedStackKey is not null &&
            !groups.Any(group =>
                ShouldProjectAsStack(group) &&
                group.EffectiveKey == _expandedStackKey))
        {
            _expandedStackKey = null;
        }

        var projected = new List<WidgetItem>();
        foreach (StackDisplayUnit unit in
                 OrderDisplayUnits(BuildDisplayUnits(groups)))
        {
            if (unit.LooseItem is { } looseItem)
            {
                projected.Add(looseItem);
                continue;
            }

            WidgetStackGroup group = unit.StackGroup!;
            string key = group.EffectiveKey;
            bool expanded = string.Equals(
                key,
                _expandedStackKey,
                StringComparison.Ordinal);
            projected.Add(CreateStackItem(group, expanded));
            if (!expanded)
            {
                continue;
            }

            foreach (WidgetItem item in group.Items)
            {
                item.IsStackChild = true;
                projected.Add(item);
            }
        }

        ReconcileStackDisplayItems(projected);
    }

    private List<StackDisplayUnit> BuildDisplayUnits(
        IReadOnlyList<WidgetStackGroup> groups)
    {
        var units = new List<StackDisplayUnit>();
        foreach (WidgetStackGroup group in groups)
        {
            bool useStack = ShouldProjectAsStack(group);
            if (useStack)
            {
                units.Add(new StackDisplayUnit(
                    group.EffectiveKey,
                    group,
                    null));
                continue;
            }

            units.AddRange(group.Items.Select(item =>
                new StackDisplayUnit(
                    GetLooseItemOrderKey(item),
                    null,
                    item)));
        }

        return units;
    }

    private bool ShouldProjectAsStack(
        WidgetStackGroup group)
    {
        int minimumCount = group.ForceStack
            ? 2
            : FileStackThreshold;
        return group.CanStack &&
            !_disabledStacks.Contains(group.EffectiveKey) &&
            group.Items.Count >= minimumCount;
    }

    private IReadOnlyList<WidgetStackGroup>
        ApplyStackMemberOverrides(
            IReadOnlyList<WidgetStackGroup> automaticGroups)
    {
        if (_stackMemberOverrides.Count == 0)
        {
            return automaticGroups;
        }

        Dictionary<string, WidgetItem> itemsByPath = Items
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(
                item => NormalizeStackMemberPath(item.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var assignedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var forcedByStack =
            new Dictionary<string, List<WidgetItem>>(
                StringComparer.Ordinal);
        foreach ((string stackKey, List<string> paths) in
                 _stackMemberOverrides)
        {
            var members = new List<WidgetItem>();
            foreach (string path in paths)
            {
                string normalizedPath =
                    NormalizeStackMemberPath(path);
                if (assignedPaths.Add(normalizedPath) &&
                    itemsByPath.TryGetValue(
                        normalizedPath,
                        out WidgetItem? item))
                {
                    members.Add(item);
                }
            }

            if (members.Count > 0)
            {
                forcedByStack[stackKey] = members;
            }
        }

        var result = new List<WidgetStackGroup>();
        foreach (WidgetStackGroup group in automaticGroups)
        {
            List<WidgetItem> members = group.Items
                .Where(item => !assignedPaths.Contains(
                    NormalizeStackMemberPath(item.Path)))
                .ToList();
            bool hasForcedMembers =
                forcedByStack.Remove(
                    group.EffectiveKey,
                    out List<WidgetItem>? forcedMembers);
            if (hasForcedMembers &&
                forcedMembers is not null)
            {
                members.AddRange(forcedMembers);
            }

            if (members.Count > 0)
            {
                result.Add(group with
                {
                    Items = members,
                    ForceStack = hasForcedMembers
                });
            }
        }

        foreach ((string stackKey, List<WidgetItem> members) in
                 forcedByStack)
        {
            bool manual = stackKey.StartsWith(
                ManualStackKeyPrefix,
                StringComparison.Ordinal);
            WidgetStackCategory category =
                !manual &&
                Enum.TryParse(
                    stackKey,
                    ignoreCase: false,
                    out WidgetStackCategory parsedCategory)
                    ? parsedCategory
                    : WidgetStackCategory.Other;
            result.Add(new WidgetStackGroup(
                category,
                members,
                stackKey,
                manual
                    ? _localizationService.T(
                        "Widget.Stack.ManualDefaultName")
                    : null,
                CanStack: true,
                ForceStack: true));
        }

        return result;
    }

    private List<WidgetItem> NormalizeStackMembers(
        IEnumerable<WidgetItem> items)
    {
        return items
            .Where(item =>
                item is not WidgetStackItem &&
                Items.Contains(item) &&
                !string.IsNullOrWhiteSpace(item.Path))
            .DistinctBy(
                item => NormalizeStackMemberPath(item.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int ResolveManualStackInsertionIndex(
        List<string> currentOrder,
        IReadOnlyList<WidgetItem> members)
    {
        int insertionIndex = currentOrder.Count;
        foreach (WidgetItem member in members)
        {
            string unitKey = GetLooseItemOrderKey(member);
            int index = currentOrder
                .IndexOf(unitKey);
            if (index >= 0)
            {
                insertionIndex = Math.Min(
                    insertionIndex,
                    index);
                continue;
            }

            WidgetStackItem? containingStack =
                _stackItems.Values.FirstOrDefault(stack =>
                    stack.Members.Any(candidate =>
                        ReferenceEquals(candidate, member)));
            if (containingStack is not null)
            {
                index = currentOrder.IndexOf(
                    containingStack.StackKey);
                if (index >= 0)
                {
                    insertionIndex = Math.Min(
                        insertionIndex,
                        index + 1);
                }
            }
        }

        return insertionIndex;
    }

    private void RemoveMemberOverrides(
        IReadOnlySet<string> paths)
    {
        foreach (string stackKey in
                 _stackMemberOverrides.Keys.ToArray())
        {
            _stackMemberOverrides[stackKey].RemoveAll(path =>
                paths.Contains(
                    NormalizeStackMemberPath(path)));
            if (_stackMemberOverrides[stackKey].Count == 0)
            {
                _stackMemberOverrides.Remove(stackKey);
            }
        }
    }

    private void UpdateStackMemberOverridePath(
        string sourcePath,
        string destinationPath)
    {
        string normalizedSource =
            NormalizeStackMemberPath(sourcePath);
        string normalizedDestination =
            NormalizeStackMemberPath(destinationPath);
        bool changed = false;
        foreach (List<string> paths in
                 _stackMemberOverrides.Values)
        {
            int index = paths.FindIndex(path =>
                string.Equals(
                    NormalizeStackMemberPath(path),
                    normalizedSource,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            paths[index] = normalizedDestination;
            changed = true;
        }

        if (changed)
        {
            PersistStackCustomizations();
        }
    }

    private void RemoveStackMemberOverridePaths(
        IEnumerable<string> paths)
    {
        HashSet<string> normalizedPaths = paths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeStackMemberPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedPaths.Count == 0)
        {
            return;
        }

        int entryCount = _stackMemberOverrides.Count;
        int pathCount = _stackMemberOverrides.Values
            .Sum(value => value.Count);
        RemoveMemberOverrides(normalizedPaths);
        if (entryCount != _stackMemberOverrides.Count ||
            pathCount != _stackMemberOverrides.Values
                .Sum(value => value.Count))
        {
            PersistStackCustomizations();
        }
    }

    private static string NormalizeStackMemberPath(
        string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private List<StackDisplayUnit> OrderDisplayUnits(
        IReadOnlyList<StackDisplayUnit> units)
    {
        if (_stackOrder.Count == 0)
        {
            return units.ToList();
        }

        var ordered = new List<StackDisplayUnit>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in _stackOrder)
        {
            StackDisplayUnit? unit = units.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.OrderKey,
                    key,
                    StringComparison.Ordinal));
            if (unit is not null && known.Add(unit.OrderKey))
            {
                ordered.Add(unit);
            }
        }

        foreach (StackDisplayUnit unit in units)
        {
            if (known.Add(unit.OrderKey))
            {
                ordered.Add(unit);
            }
        }

        return ordered;
    }

    private WidgetStackItem CreateStackItem(WidgetStackGroup group, bool expanded)
    {
        string key = group.EffectiveKey;
        string name = group.DisplayName ?? GetStackCategoryName(group.Category);
        if (_stackNameOverrides.TryGetValue(key, out string? customName) && !string.IsNullOrWhiteSpace(customName))
        {
            name = customName;
        }
        if (!_stackItems.TryGetValue(key, out var stack))
        {
            stack = new WidgetStackItem
            {
                Category = group.Category,
                StackKey = key
            };
            _stackItems[key] = stack;
        }

        stack.Update(
            group.Items,
            name,
            _localizationService.Format("Widget.Stack.ItemCount", group.Items.Count),
            _localizationService.T(expanded
                ? "Widget.Stack.State.Expanded"
                : "Widget.Stack.State.Collapsed"),
            _localizationService.T("Widget.Stack.Collapse"),
            expanded,
            IconTileWidth,
            IconTileHeight,
            IconTileMargin,
            IconTilePadding,
            IconImageSize,
            Math.Clamp(Math.Round(IconImageSize * 0.76), 14, IconImageSize),
            IconLabelMaxWidth,
            IconLabelFontSize,
            ListItemMargin,
            ListItemPadding,
            ListIconSize);
        return stack;
    }

    private void RefreshStackLayoutMetrics()
    {
        foreach (WidgetStackItem stack in _stackItems.Values)
        {
            stack.UpdateLayoutMetrics(
                IconTileWidth,
                IconTileHeight,
                IconTileMargin,
                IconTilePadding,
                IconImageSize,
                Math.Clamp(Math.Round(IconImageSize * 0.76), 14, IconImageSize),
                IconLabelMaxWidth,
                IconLabelFontSize,
                ListItemMargin,
                ListItemPadding,
                ListIconSize);
        }
    }

    private void ReconcileStackDisplayItems(IReadOnlyList<WidgetItem> desired)
    {
        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            WidgetItem desiredItem = desired[targetIndex];
            if (targetIndex < _stackDisplayItems.Count &&
                ReferenceEquals(_stackDisplayItems[targetIndex], desiredItem))
            {
                continue;
            }

            int existingIndex = IndexOfReference(_stackDisplayItems, desiredItem, targetIndex + 1);
            if (existingIndex >= 0)
            {
                _stackDisplayItems.Move(existingIndex, targetIndex);
            }
            else
            {
                _stackDisplayItems.Insert(targetIndex, desiredItem);
            }
        }

        while (_stackDisplayItems.Count > desired.Count)
        {
            _stackDisplayItems.RemoveAt(_stackDisplayItems.Count - 1);
        }
    }

    private static int IndexOfReference(
        IReadOnlyList<WidgetItem> items,
        WidgetItem candidate,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], candidate))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record StackDisplayUnit(
        string OrderKey,
        WidgetStackGroup? StackGroup,
        WidgetItem? LooseItem);

    private string GetStackCategoryName(WidgetStackCategory category) =>
        _localizationService.T($"Widget.Stack.Category.{category}");

    private void ScheduleStackDateBoundaryRefresh()
    {
        _stackDateBoundaryTimer ??= _dispatcherQueue.CreateTimer();
        _stackDateBoundaryTimer.Stop();
        _stackDateBoundaryTimer.Tick -= StackDateBoundaryTimer_Tick;

        bool usesDateGrouping = FileStackGroupBy is
            SettingsService.FileStackGroupByDateAdded or
            SettingsService.FileStackGroupByDateModified;
        if (!FileStacksEnabled || !usesDateGrouping)
        {
            return;
        }

        DateTime now = DateTime.Now;
        _stackDateBoundaryTimer.Interval = now.Date.AddDays(1).AddSeconds(1) - now;
        _stackDateBoundaryTimer.IsRepeating = false;
        _stackDateBoundaryTimer.Tick += StackDateBoundaryTimer_Tick;
        _stackDateBoundaryTimer.Start();
    }

    private void StackDateBoundaryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= StackDateBoundaryTimer_Tick;
        RebuildStackDisplayItems();
        ScheduleStackDateBoundaryRefresh();
    }
}
