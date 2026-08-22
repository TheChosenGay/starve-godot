using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GodotClient.Game;

public sealed record ItemView(int Kind, string Name, int Count, Color Color, Texture2D? Icon = null);
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
    public event Action<string>? CraftPressed;
    public event Action? CancelCraftPressed;
    public event Action? SleepPressed;
    public event Action? CancelSleepPressed;

    private Label? _status;
    private VitalBar? _vitalsBar;
    private RichTextLabel? _log;
    private HBoxContainer? _bag;
    private readonly List<int> _slotKinds = new();
    private Button? _bagUse;
    private Button? _bagEquip;
    private Button? _bagDrop;
    private Button? _bagSplit;
    private GridContainer? _craftList;
    private Button? _craftCancel;
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
    private int _lastSlotCount;
    private IReadOnlyList<RecipeView> _lastRecipes = Array.Empty<RecipeView>();
    private CraftingView? _lastCrafting;

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

        GetViewport().SizeChanged += OnViewportSizeChanged;
        FitToViewport();
        CallDeferred(MethodName.RelayoutDrawers);
    }

    public override void _ExitTree()
    {
        var vp = GetViewport();
        if (vp is not null)
            vp.SizeChanged -= OnViewportSizeChanged;
    }

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
            RelayoutDrawers();
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnViewportSizeChanged() => RelayoutDrawers();

    private void FitToViewport()
    {
        var size = GetViewportRect().Size;
        if (size.X < 2 || size.Y < 2) return;
        Position = Vector2.Zero;
        Size = size;
    }

    private void RelayoutDrawers()
    {
        FitToViewport();
        FitBottomBar();
        FitLog();
        ApplyCraftOpenState();
        if (_fps is not null)
            _fps.Position = new Vector2(Math.Max(8, Size.X - 280), 8);
    }

    private void FitBottomBar()
    {
        if (_bottomBar is null) return;
        var min = _bottomBar.GetCombinedMinimumSize();
        var w = Math.Min(Math.Max(min.X, 1), Math.Max(80, Size.X - 16));
        var h = Math.Max(min.Y, 52f);
        ResetAnchors(_bottomBar);
        _bottomBar.Size = new Vector2(w, h);
        _bottomBar.Position = new Vector2(Mathf.Floor((Size.X - w) * 0.5f), Size.Y - BottomMargin - h);
    }

    private void FitLog()
    {
        if (_log is null || _bottomBar is null) return;
        ResetAnchors(_log);
        _log.Position = new Vector2(_bottomBar.Position.X, _bottomBar.Position.Y - 22);
        _log.Size = new Vector2(Math.Max(160, _bottomBar.Size.X), 20);
    }

    private static void ResetAnchors(Control control)
    {
        control.AnchorLeft = 0;
        control.AnchorTop = 0;
        control.AnchorRight = 0;
        control.AnchorBottom = 0;
        control.OffsetLeft = 0;
        control.OffsetTop = 0;
        control.OffsetRight = 0;
        control.OffsetBottom = 0;
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
        if (!_craftOpen) _selectedRecipeId = null;
        RelayoutDrawers();
    }

    private void ApplyCraftOpenState()
    {
        if (_craftTab is null || _craftRail is null || _craftCard is null) return;
        var midY = Size.Y * 0.38f;
        ResetAnchors(_craftTab);
        _craftTab.Text = _craftOpen ? "收\n起" : "制\n作";
        _craftTab.Position = new Vector2(0, midY);
        _craftTab.Size = new Vector2(CraftTabWidth, 96);

        _craftRail.Visible = _craftOpen;
        var railW = 0f;
        if (_craftOpen)
        {
            ResetAnchors(_craftRail);
            var min = _craftRail.GetCombinedMinimumSize();
            railW = Math.Max(CraftSlot.Cell * 2 + 22, min.X);
            var railH = Math.Max(min.Y, CraftSlot.Cell + 16);
            var top = midY - railH * 0.5f;
            var maxBottom = (_bottomBar?.Position.Y ?? Size.Y) - 10;
            if (top + railH > maxBottom) top = Math.Max(8, maxBottom - railH);
            _craftRail.Size = new Vector2(railW, railH);
            _craftRail.Position = new Vector2(CraftTabWidth + 4, top);
        }

        var showCard = _craftOpen && (_selectedRecipeId is not null || _lastCrafting is not null);
        _craftCard.Visible = showCard;
        if (showCard)
        {
            RebuildCraftCard();
            ResetAnchors(_craftCard);
            var min = _craftCard.GetCombinedMinimumSize();
            var cardW = Math.Max(168, min.X);
            var cardH = Math.Max(min.Y, 96);
            _craftCard.Size = new Vector2(cardW, cardH);
            _craftCard.Position = new Vector2(CraftTabWidth + railW + 6, midY - cardH * 0.4f);
        }
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
        foreach (var button in _gameplayButtons) button.Disabled = disabled;
        foreach (var button in _craftButtons) button.Disabled = disabled;
        RefreshBagButtons();
    }

    public void SetToolState(bool canChop, bool canMine)
    {
        _ = canChop;
        _ = canMine;
    }

    public void RenderInventory(IReadOnlyList<ItemView> items, IReadOnlyCollection<int> equippedKinds, int slots)
    {
        if (_bag is null) return;
        _lastItems = items;
        _lastEquipped = equippedKinds;
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
                _bag.AddChild(slot);
                _slotKinds.Add(0);
            }
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
        RefreshBagButtons();
        CallDeferred(MethodName.RelayoutDrawers);
    }

    public void RenderCraft(IReadOnlyList<RecipeView> recipes, CraftingView? crafting)
    {
        if (_craftList is null) return;
        _lastRecipes = recipes;
        _lastCrafting = crafting;
        _craftingActive = crafting is not null;
        foreach (var child in _craftList.GetChildren()) child.QueueFree();
        _craftButtons.Clear();
        if (crafting is not null && !_craftOpen)
        {
            _craftOpen = true;
            _selectedRecipeId = crafting.RecipeId;
            RelayoutDrawers();
        }

        foreach (var recipe in recipes)
        {
            var slot = new CraftSlot();
            slot.Configure(recipe.Icon, recipe.OutputName, recipe.Id == _selectedRecipeId, recipe.CanCraft);
            var id = recipe.Id;
            slot.Pressed += () =>
            {
                _selectedRecipeId = id;
                if (!_craftOpen) _craftOpen = true;
                RelayoutDrawers();
            };
            _craftButtons.Add(slot);
            _craftList.AddChild(slot);
        }
        if (_craftOpen) CallDeferred(MethodName.RelayoutDrawers);
    }

    private void SelectBagSlot(int slot)
    {
        if (_selectedSlot == slot) return;
        _selectedSlot = slot;
        RenderInventory(_lastItems, _lastEquipped, _lastSlotCount);
    }

    private void RebuildCraftCard()
    {
        if (_craftCard is null) return;
        var body = _craftCard.GetChild(0).GetChild(0);
        foreach (var child in body.GetChildren()) child.QueueFree();
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
        var craft = MakeButton(recipe.CanCraft ? "制作" : "尝试制作", () => CraftPressed?.Invoke(recipe.Id));
        craft.CustomMinimumSize = new Vector2(96, 32);
        craft.Disabled = _interactionsDisabled;
        _craftButtons.Add(craft);
        box.AddChild(craft);
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

    private static Button MakeButton(string text, Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(text.Length >= 2 ? 52 : 40, 32),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
        };
        b.Pressed += onPressed;
        return b;
    }
}
