using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GodotClient.Game;

public sealed record ItemView(
    int Kind,
    string Name,
    int Count,
    Color Color,
    Texture2D? Icon = null,
    int Durability = 0,
    int MaxDurability = 0);
public sealed record EquipSlotView(string Id, string Label, ItemView? Item);
public sealed record IngredientView(string Name, int Have, int Need, Texture2D? Icon = null, Color Color = default);
public sealed record RecipeView(
    string Id,
    string OutputName,
    int Ticks,
    string StationLabel,
    bool CanCraft,
    IReadOnlyList<IngredientView> Ingredients,
    Texture2D? Icon = null);
public sealed record CraftingView(string RecipeId, long TicksLeft, long Ticks);

/// <summary>
/// HUD：左上生命圆、底栏一行背包、左侧拉伸制作抽屉。
/// </summary>
public partial class Hud : Control
{
    private const float BottomMargin = 8f;
    private const float SideMargin = 10f;
    private const float CraftTabWidth = 28f;

#pragma warning disable CS0067 // 采集/攻击仍由空格与 F 键走 GameRoot，HUD 不再挂按钮。
    public event Action? GatherPressed;
    public event Action? AttackPressed;
    public event Action? ChopPressed;
    public event Action? MinePressed;
    public event Action? PickupPressed;
#pragma warning restore CS0067
    public event Action? DemolishPressed;
    public event Action<int>? BuildPressed;
    public event Action<int>? BagUsePressed;
    public event Action<int>? BagEquipPressed;
    public event Action<int>? BagDropPressed;
    public event Action<int>? BagSplitPressed;
    public event Action<string>? WornSlotUnequipPressed;
    public event Action<string>? CraftPressed;
    public event Action? CancelCraftPressed;
    public event Action? SleepPressed;
    public event Action? CancelSleepPressed;
    public event Action? UiClicked;
    public event Action? CraftOpened;

    private Label? _status;
    private VitalBar? _vitalsBar;
    private RichTextLabel? _log;
    private HBoxContainer? _bag;
    private HBoxContainer? _equip;
    private readonly Dictionary<string, InventorySlot> _wornSlots = new();
    private readonly List<int> _slotKinds = new();
    private Button? _bagUse;
    private Button? _bagEquip;
    private Button? _bagDrop;
    private Button? _bagSplit;
    private GridContainer? _craftList;
    private Button? _craftCancel;
    private Button? _craftConfirm;
    private Button? _craftTab;
    private PanelContainer? _craftRail;
    private PanelContainer? _craftCard;
    private PanelContainer? _bottomBar;
    private readonly List<Button> _gameplayButtons = new();
    private readonly List<Button> _craftButtons = new();
    private bool _interactionsDisabled;
    private bool _craftingActive;
    private bool _craftOpen;
    private int _selectedSlot;
    private string? _selectedRecipeId;
    private Label? _fps;
    private double _fpsTimer;
    private string? _lastStatusText;
    private IReadOnlyList<ItemView> _lastItems = Array.Empty<ItemView>();
    private IReadOnlyCollection<int> _lastEquipped = Array.Empty<int>();
    private IReadOnlyList<EquipSlotView> _lastWorn = Array.Empty<EquipSlotView>();
    private int _lastSlotCount;
    private IReadOnlyList<RecipeView> _lastRecipes = Array.Empty<RecipeView>();
    private CraftingView? _lastCrafting;
    private int _lastCardFingerprint = int.MinValue;
    private bool _relayouting;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = HudTheme.Create();

        AddChild(BuildTopLeft());
        AddChild(BuildLog());
        AddChild(BuildBottomBar());
        AddChild(BuildCraftDrawer());
        AddChild(BuildFps());

        CallDeferred(MethodName.RelayoutDrawers);
    }

    public void Relayout() => RelayoutDrawers();

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.C)
        {
            ToggleCraft();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && _craftOpen)
        {
            _craftOpen = false;
            _selectedRecipeId = null;
            _lastCardFingerprint = int.MinValue;
            RelayoutDrawers();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>点在制作/背包等交互板上时，世界点击不能再落到实体上。</summary>
    public bool HitsInteractive(Vector2 screen)
    {
        return ContainsScreen(_craftTab, screen) ||
               ContainsScreen(_craftRail, screen) ||
               ContainsScreen(_craftCard, screen) ||
               ContainsScreen(_bottomBar, screen);
    }

    private static bool ContainsScreen(Control? control, Vector2 screen) =>
        control is { Visible: true } && control.GetGlobalRect().HasPoint(screen);

    private void RelayoutDrawers()
    {
        if (_relayouting) return;
        if (GetParent() is Control parent && parent.Size.X < 2)
        {
            CallDeferred(MethodName.RelayoutDrawers);
            return;
        }
        _relayouting = true;
        try
        {
            SetAnchorsPreset(LayoutPreset.FullRect);
            PinBottomBar();
            PinLog();
            PinFps();
            PinCraft();
        }
        finally
        {
            _relayouting = false;
        }
    }

    private void PinBottomBar()
    {
        if (_bottomBar is null) return;
        _bottomBar.Visible = true;
        var min = _bottomBar.GetCombinedMinimumSize();
        var w = Math.Max(min.X, 80);
        var h = Math.Max(min.Y, 52f);
        _bottomBar.AnchorLeft = 0.5f;
        _bottomBar.AnchorRight = 0.5f;
        _bottomBar.AnchorTop = 1f;
        _bottomBar.AnchorBottom = 1f;
        _bottomBar.OffsetLeft = -w * 0.5f;
        _bottomBar.OffsetRight = w * 0.5f;
        _bottomBar.OffsetTop = -BottomMargin - h;
        _bottomBar.OffsetBottom = -BottomMargin;
    }

    private void PinLog()
    {
        if (_log is null) return;
        var w = Math.Max(160, BarWidth());
        _log.AnchorLeft = 0.5f;
        _log.AnchorRight = 0.5f;
        _log.AnchorTop = 1f;
        _log.AnchorBottom = 1f;
        _log.OffsetLeft = -w * 0.5f;
        _log.OffsetRight = w * 0.5f;
        _log.OffsetTop = -BottomMargin - BarHeight() - 22;
        _log.OffsetBottom = -BottomMargin - BarHeight() - 2;
    }

    private void PinFps()
    {
        if (_fps is null) return;
        _fps.AnchorLeft = 1f;
        _fps.AnchorRight = 1f;
        _fps.AnchorTop = 0;
        _fps.AnchorBottom = 0;
        _fps.OffsetLeft = -96;
        _fps.OffsetRight = -8;
        _fps.OffsetTop = 8;
        _fps.OffsetBottom = 30;
    }

    private float BarWidth()
    {
        if (_bottomBar is null) return 80;
        return Math.Max(_bottomBar.GetCombinedMinimumSize().X, 80);
    }

    private float BarHeight()
    {
        if (_bottomBar is null) return 52;
        return Math.Max(_bottomBar.GetCombinedMinimumSize().Y, 52);
    }

    private Control BuildTopLeft()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        column.MouseFilter = MouseFilterEnum.Ignore;
        _vitalsBar = new VitalBar();
        column.AddChild(_vitalsBar);
        _status = new Label
        {
            Modulate = HudTheme.ParchmentDim,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(120, 0),
        };
        _status.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(_status);
        column.Name = "TopLeft";
        column.SetAnchorsPreset(LayoutPreset.TopLeft);
        column.OffsetLeft = SideMargin;
        column.OffsetTop = SideMargin;
        column.GrowVertical = GrowDirection.End;
        return column;
    }

    private Control BuildLog()
    {
        _log = new RichTextLabel
        {
            Name = "Log",
            BbcodeEnabled = true,
            CustomMinimumSize = new Vector2(160, 20),
            ScrollFollowing = true,
            FitContent = false,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0.72f),
        };
        return _log;
    }

    private Control BuildBottomBar()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.Alignment = BoxContainer.AlignmentMode.Begin;
        row.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;

        _equip = new HBoxContainer();
        _equip.AddThemeConstantOverride("separation", 4);
        _equip.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        foreach (var (id, label) in new[] { ("head", "头"), ("hand", "手"), ("body", "身") })
        {
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 0);
            var caption = new Label
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                Modulate = HudTheme.ParchmentDim,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            caption.AddThemeFontSizeOverride("font_size", 11);
            col.AddChild(caption);
            var slot = new InventorySlot();
            slot.TooltipText = $"{label}（右键卸下）";
            slot.RightClicked += () => WornSlotUnequipPressed?.Invoke(id);
            _wornSlots[id] = slot;
            col.AddChild(slot);
            _equip.AddChild(col);
        }
        row.AddChild(_equip);
        row.AddChild(MakeDivider());

        _bag = new HBoxContainer();
        _bag.AddThemeConstantOverride("separation", 4);
        _bag.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        row.AddChild(_bag);

        row.AddChild(MakeDivider());
        _bagUse = MakeButton("用", () => WithBagSlot(_bagUse, BagUsePressed));
        _bagEquip = MakeButton("装", () => WithBagSlot(_bagEquip, BagEquipPressed));
        _bagDrop = MakeButton("丢", () => WithBagSlot(_bagDrop, BagDropPressed));
        _bagSplit = MakeButton("拆", () => WithBagSlot(_bagSplit, BagSplitPressed));
        row.AddChild(_bagUse);
        row.AddChild(_bagEquip);
        row.AddChild(_bagDrop);
        row.AddChild(_bagSplit);
        row.AddChild(MakeDivider());
        row.AddChild(MakeButton("火", () => BuildPressed?.Invoke(1)));
        row.AddChild(MakeButton("墙", () => BuildPressed?.Invoke(2)));
        row.AddChild(TrackGameplay(MakeButton("睡", () => SleepPressed?.Invoke())));
        row.AddChild(TrackGameplay(MakeButton("醒", () => CancelSleepPressed?.Invoke())));
        row.AddChild(MakeButton("拆建", () => DemolishPressed?.Invoke()));

        var panel = WrapPanel(row, 6);
        panel.Name = "BottomBar";
        panel.ClipContents = false;
        panel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _bottomBar = panel;
        return panel;
    }

    private Control BuildCraftDrawer()
    {
        _craftTab = new Button
        {
            Text = "制\n作",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(CraftTabWidth, 96),
        };
        _craftTab.AddThemeFontSizeOverride("font_size", 14);
        _craftTab.Pressed += ToggleCraft;
        AddChild(_craftTab);

        _craftList = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _craftList.AddThemeConstantOverride("h_separation", 6);
        _craftList.AddThemeConstantOverride("v_separation", 6);
        _craftRail = WrapPanel(_craftList, 8);
        _craftRail.Name = "CraftRail";
        _craftRail.Visible = false;
        AddChild(_craftRail);

        var cardBody = new VBoxContainer { Name = "CraftCardBody" };
        cardBody.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _craftCard = WrapPanel(cardBody, 8);
        _craftCard.Name = "CraftCard";
        _craftCard.Visible = false;
        AddChild(_craftCard);
        return _craftTab;
    }

    private Label BuildFps()
    {
        _fps = new Label
        {
            Name = "Fps",
            Modulate = HudTheme.ParchmentDim,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "FPS --",
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(80, 22),
        };
        return _fps;
    }

    private void ToggleCraft()
    {
        _craftOpen = !_craftOpen;
        if (!_craftOpen)
        {
            _selectedRecipeId = null;
            _lastCardFingerprint = int.MinValue;
            UiClicked?.Invoke();
        }
        else
        {
            CraftOpened?.Invoke();
        }
        RelayoutDrawers();
    }

    private void PinCraft()
    {
        if (_craftTab is null || _craftRail is null || _craftCard is null) return;
        _craftTab.Text = _craftOpen ? "收\n起" : "制\n作";
        _craftTab.AnchorLeft = 0;
        _craftTab.AnchorRight = 0;
        _craftTab.AnchorTop = 0.42f;
        _craftTab.AnchorBottom = 0.42f;
        _craftTab.OffsetLeft = 0;
        _craftTab.OffsetRight = CraftTabWidth;
        _craftTab.OffsetTop = -48;
        _craftTab.OffsetBottom = 48;

        _craftRail.Visible = _craftOpen;
        var railW = 0f;
        if (_craftOpen)
        {
            var min = _craftRail.GetCombinedMinimumSize();
            railW = Math.Max(CraftSlot.Cell * 2 + 22, min.X);
            var railH = Math.Max(min.Y, CraftSlot.Cell + 16);
            _craftRail.AnchorLeft = 0;
            _craftRail.AnchorRight = 0;
            _craftRail.AnchorTop = 0.42f;
            _craftRail.AnchorBottom = 0.42f;
            _craftRail.OffsetLeft = CraftTabWidth + 4;
            _craftRail.OffsetRight = CraftTabWidth + 4 + railW;
            _craftRail.OffsetTop = -railH * 0.5f;
            _craftRail.OffsetBottom = railH * 0.5f;
        }

        var showCard = _craftOpen && (_selectedRecipeId is not null || _lastCrafting is not null);
        _craftCard.Visible = showCard;
        if (!showCard) return;
        var cardMin = _craftCard.GetCombinedMinimumSize();
        var cardW = Math.Max(168, cardMin.X);
        var cardH = Math.Max(cardMin.Y, 96);
        _craftCard.AnchorLeft = 0;
        _craftCard.AnchorRight = 0;
        _craftCard.AnchorTop = 0.42f;
        _craftCard.AnchorBottom = 0.42f;
        _craftCard.OffsetLeft = CraftTabWidth + railW + 10;
        _craftCard.OffsetRight = CraftTabWidth + railW + 10 + cardW;
        _craftCard.OffsetTop = -cardH * 0.4f;
        _craftCard.OffsetBottom = cardH * 0.6f;
    }

    private void RefreshCraftCard(bool relayout)
    {
        var fingerprint = CraftCardFingerprint();
        if (fingerprint != _lastCardFingerprint)
        {
            _lastCardFingerprint = fingerprint;
            RebuildCraftCard();
        }
        if (relayout) RelayoutDrawers();
    }

    private int CraftCardFingerprint()
    {
        var hash = new HashCode();
        hash.Add(_selectedRecipeId);
        if (_lastCrafting is { } crafting)
        {
            hash.Add(crafting.RecipeId);
            hash.Add(crafting.TicksLeft);
            hash.Add(crafting.Ticks);
        }
        var recipe = _lastRecipes.FirstOrDefault(r => r.Id == _selectedRecipeId)
                     ?? (_lastCrafting is { } active
                         ? _lastRecipes.FirstOrDefault(r => r.Id == active.RecipeId)
                         : null);
        if (recipe is null) return hash.ToHashCode();
        hash.Add(recipe.CanCraft);
        hash.Add(recipe.StationLabel);
        foreach (var ingredient in recipe.Ingredients)
        {
            hash.Add(ingredient.Name);
            hash.Add(ingredient.Have);
            hash.Add(ingredient.Need);
        }
        return hash.ToHashCode();
    }

    private static Control MakeDivider() =>
        new Control { CustomMinimumSize = new Vector2(8, 1), MouseFilter = MouseFilterEnum.Ignore };

    private static PanelContainer WrapPanel(Control content, int pad = 8)
    {
        var panel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        var tight = HudTheme.MakePanelStyle(HudTheme.WoodPanel, new Color(1, 1, 1, 0.12f));
        tight.ContentMarginLeft = pad;
        tight.ContentMarginTop = pad;
        tight.ContentMarginRight = pad;
        tight.ContentMarginBottom = pad;
        panel.AddThemeStyleboxOverride("panel", tight);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 0);
        margin.AddThemeConstantOverride("margin_top", 0);
        margin.AddThemeConstantOverride("margin_right", 0);
        margin.AddThemeConstantOverride("margin_bottom", 0);
        margin.AddChild(content);
        panel.AddChild(margin);
        return panel;
    }

    public override void _Process(double delta)
    {
        _fpsTimer += delta;
        if (_fpsTimer >= 0.3 && _fps is not null)
        {
            _fpsTimer = 0;
            _fps.Text = $"FPS {Engine.GetFramesPerSecond()}";
        }
    }

    public void SetStatus(string text)
    {
        if (text == _lastStatusText) return;
        _lastStatusText = text;
        if (_status is not null) _status.Text = text;
    }

    public void Log(string line)
    {
        if (_log is not null) _log.AppendText(line + "\n");
    }

    public void SetVitals(HudVitalsViewModel vitals) =>
        _vitalsBar?.SetVitals(vitals);

    public void SetInteractionsDisabled(bool disabled)
    {
        _interactionsDisabled = disabled;
        DisableLive(_gameplayButtons, disabled);
        DisableLive(_craftButtons, disabled);
        if (GodotObject.IsInstanceValid(_craftCancel)) _craftCancel!.Disabled = disabled;
        if (GodotObject.IsInstanceValid(_craftConfirm)) _craftConfirm!.Disabled = disabled;
        RefreshBagButtons();
    }

    /// <summary>
    /// 材料卡重建会 QueueFree 旧按钮。若还留在列表里，下一帧写 Disabled 会抛，
    /// GameRoot._Process 后半段（本地移动、相机）整段被跳过，角色就冻住；雨和火是 GPU，看着还在动。
    /// </summary>
    private static void DisableLive(List<Button> buttons, bool disabled)
    {
        for (var i = buttons.Count - 1; i >= 0; i--)
        {
            if (!GodotObject.IsInstanceValid(buttons[i]))
            {
                buttons.RemoveAt(i);
                continue;
            }
            buttons[i].Disabled = disabled;
        }
    }

    public void SetToolState(bool canChop, bool canMine)
    {
        _ = canChop;
        _ = canMine;
    }

    public void RenderInventory(
        IReadOnlyList<ItemView> items,
        IReadOnlyCollection<int> equippedKinds,
        int slots,
        IReadOnlyList<EquipSlotView>? worn = null)
    {
        if (_bag is null) return;
        _lastItems = items;
        _lastEquipped = equippedKinds;
        _lastWorn = worn ?? Array.Empty<EquipSlotView>();
        _lastSlotCount = slots;

        var reuse = _bag.GetChildCount() == slots && _bag.GetChildren().All(c => c is InventorySlot);
        if (!reuse)
        {
            foreach (var child in _bag.GetChildren()) child.QueueFree();
            _slotKinds.Clear();
            for (var i = 0; i < slots; i++)
            {
                var slot = new InventorySlot();
                var index = i;
                slot.Pressed += () => SelectBagSlot(index);
                slot.RightClicked += () => OnBagRightClicked(index);
                _bag.AddChild(slot);
                _slotKinds.Add(0);
            }
            CallDeferred(MethodName.RelayoutDrawers);
        }

        for (var i = 0; i < slots; i++)
        {
            var item = i < items.Count ? items[i] : null;
            var equipped = item is not null && equippedKinds.Contains(item.Kind);
            if (_bag.GetChild(i) is InventorySlot slot)
                slot.Configure(item, equipped, i == _selectedSlot);
            if (_slotKinds.Count <= i) _slotKinds.Add(0);
            _slotKinds[i] = item is { Kind: > 0, Count: > 0 } ? item.Kind : 0;
        }
        foreach (var view in _lastWorn)
        {
            if (!_wornSlots.TryGetValue(view.Id, out var slot)) continue;
            slot.Configure(view.Item, view.Item is not null, false);
            slot.TooltipText = view.Item is null
                ? $"{view.Label}（空）"
                : view.Item.MaxDurability > 0
                    ? $"{view.Label} {view.Item.Name} 耐久 {view.Item.Durability}/{view.Item.MaxDurability}（右键卸下）"
                    : $"{view.Label} {view.Item.Name}（右键卸下）";
        }
        RefreshBagButtons();
    }

    public void RenderCraft(IReadOnlyList<RecipeView> recipes, CraftingView? crafting)
    {
        if (_craftList is null) return;
        _lastRecipes = recipes;
        _lastCrafting = crafting;
        _craftingActive = crafting is not null;
        var openedForCraft = crafting is not null && !_craftOpen;
        if (openedForCraft)
        {
            _craftOpen = true;
            _selectedRecipeId = crafting!.RecipeId;
        }

        SyncCraftSlots(recipes);
        if (openedForCraft)
        {
            _lastCardFingerprint = int.MinValue;
            RefreshCraftCard(relayout: false);
            RelayoutDrawers();
            return;
        }

        if (_craftOpen) RefreshCraftCard(relayout: false);
    }

    private void SyncCraftSlots(IReadOnlyList<RecipeView> recipes)
    {
        if (_craftList is null) return;
        while (_craftList.GetChildCount() > recipes.Count)
        {
            var extra = _craftList.GetChild(_craftList.GetChildCount() - 1);
            _craftButtons.Remove((Button)extra);
            extra.QueueFree();
        }
        for (var i = 0; i < recipes.Count; i++)
        {
            var recipe = recipes[i];
            CraftSlot slot;
            if (i < _craftList.GetChildCount() && _craftList.GetChild(i) is CraftSlot existing)
            {
                slot = existing;
            }
            else
            {
                slot = new CraftSlot();
                slot.Pressed += () => OnCraftSlotPressed(slot);
                _craftButtons.Add(slot);
                _craftList.AddChild(slot);
            }
            slot.Configure(recipe.Id, recipe.Icon, recipe.OutputName, recipe.Id == _selectedRecipeId, recipe.CanCraft);
        }
    }

    private void OnCraftSlotPressed(CraftSlot slot)
    {
        if (_selectedRecipeId == slot.RecipeId && _craftOpen) return;
        UiClicked?.Invoke();
        _selectedRecipeId = slot.RecipeId;
        _craftOpen = true;
        _lastCardFingerprint = int.MinValue;
        RefreshCraftCard(relayout: false);
        CallDeferred(MethodName.RelayoutDrawers);
    }

    private void SelectBagSlot(int slot)
    {
        if (_selectedSlot == slot) return;
        _selectedSlot = slot;
        RenderInventory(_lastItems, _lastEquipped, _lastSlotCount, _lastWorn);
    }

    private void OnBagRightClicked(int slot)
    {
        SelectBagSlot(slot);
        if (slot < 0 || slot >= _slotKinds.Count || !IsEquipmentKind(_slotKinds[slot])) return;
        UiClicked?.Invoke();
        BagEquipPressed?.Invoke(slot);
    }

    private static bool IsEquipmentKind(int kind) => kind is >= 100 and <= 199;

    private void RebuildCraftCard()
    {
        if (_craftCard is null) return;
        var body = _craftCard.GetChild(0).GetChild(0);
        _craftCancel = null;
        _craftConfirm = null;
        foreach (var child in body.GetChildren())
        {
            if (child is Button button) _craftButtons.Remove(button);
            child.QueueFree();
        }
        if (body is not VBoxContainer box) return;
        box.AddThemeConstantOverride("separation", 6);

        RecipeView? recipe = null;
        if (_selectedRecipeId is not null)
            recipe = _lastRecipes.FirstOrDefault(r => r.Id == _selectedRecipeId);
        if (recipe is null && _lastCrafting is { } active)
            recipe = _lastRecipes.FirstOrDefault(r => r.Id == active.RecipeId);

        if (recipe is not null)
            box.AddChild(MakeRecipeHeader(recipe));

        if (_lastCrafting is { } crafting)
        {
            var pct = crafting.Ticks > 0 ? (int)(100 * (1 - (double)crafting.TicksLeft / crafting.Ticks)) : 0;
            box.AddChild(new ProgressBar
            {
                MinValue = 0,
                MaxValue = 100,
                Value = pct,
                ShowPercentage = true,
                CustomMinimumSize = new Vector2(168, 16),
            });
            _craftCancel = MakeButton("取消制作", () => CancelCraftPressed?.Invoke());
            _craftCancel.CustomMinimumSize = new Vector2(96, 32);
            _craftCancel.Disabled = _interactionsDisabled;
            box.AddChild(_craftCancel);
            return;
        }

        if (recipe is null) return;
        box.AddChild(new Label
        {
            Text = "材料",
            Modulate = HudTheme.ParchmentDim,
        });
        foreach (var ingredient in recipe.Ingredients)
            box.AddChild(MakeIngredientRow(ingredient));
        _craftConfirm = MakeButton(recipe.CanCraft ? "制作" : "尝试制作", () => CraftPressed?.Invoke(recipe.Id));
        _craftConfirm.CustomMinimumSize = new Vector2(96, 32);
        _craftConfirm.Disabled = _interactionsDisabled;
        box.AddChild(_craftConfirm);
    }

    private static Control MakeRecipeHeader(RecipeView recipe)
    {
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(MakeItemGlyph(recipe.Icon, recipe.OutputName, Colors.Transparent, 56));
        var titles = new VBoxContainer();
        titles.AddThemeConstantOverride("separation", 2);
        titles.AddChild(new Label
        {
            Text = recipe.OutputName,
            LabelSettings = new LabelSettings { FontSize = 16, FontColor = HudTheme.GoldOld },
        });
        titles.AddChild(new Label
        {
            Text = recipe.StationLabel,
            Modulate = recipe.CanCraft ? new Color("5dcc7a") : HudTheme.ParchmentDim,
        });
        head.AddChild(titles);
        return head;
    }

    private static Control MakeIngredientRow(IngredientView ingredient)
    {
        var ready = ingredient.Have >= ingredient.Need;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeItemGlyph(ingredient.Icon, ingredient.Name, ingredient.Color, 32));
        var text = new VBoxContainer();
        text.AddThemeConstantOverride("separation", 0);
        text.AddChild(new Label { Text = ingredient.Name });
        text.AddChild(new Label
        {
            Text = $"需要 {ingredient.Need} 个 · 已有 {ingredient.Have}",
            Modulate = ready ? new Color("5dcc7a") : HudTheme.Blood,
        });
        row.AddChild(text);
        return row;
    }

    private static Control MakeItemGlyph(Texture2D? icon, string name, Color color, float size)
    {
        var box = new PanelContainer
        {
            CustomMinimumSize = new Vector2(size, size),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddThemeStyleboxOverride("panel", HudTheme.MakeSlotStyle(false));
        if (icon is not null)
        {
            box.AddChild(new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(size - 8, size - 8),
            });
        }
        else
        {
            var label = new Label
            {
                Text = name.Length > 0 ? name[..1] : "?",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Modulate = color.A > 0 ? HudTheme.Boost(color) : HudTheme.Parchment,
            };
            box.AddChild(label);
        }
        return box;
    }

    private void RefreshBagButtons()
    {
        var has = _selectedSlot >= 0 && _selectedSlot < _slotKinds.Count && _slotKinds[_selectedSlot] > 0;
        if (_bagUse is not null) _bagUse.Disabled = _interactionsDisabled || !has;
        if (_bagEquip is not null) _bagEquip.Disabled = _interactionsDisabled || !has;
        if (_bagDrop is not null) _bagDrop.Disabled = _interactionsDisabled || !has;
        if (_bagSplit is not null) _bagSplit.Disabled = _interactionsDisabled || !has;
    }

    private void WithBagSlot(Button? btn, Action<int>? action)
    {
        if (btn is not null && _selectedSlot >= 0) action?.Invoke(_selectedSlot);
    }

    private Button TrackGameplay(Button button)
    {
        _gameplayButtons.Add(button);
        return button;
    }

    private Button MakeButton(string text, Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(text.Length >= 2 ? 52 : 40, 32),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
        };
        b.Pressed += () =>
        {
            UiClicked?.Invoke();
            onPressed();
        };
        return b;
    }
}
