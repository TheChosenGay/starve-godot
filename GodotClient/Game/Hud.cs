using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GodotClient.Game;

public sealed record ItemView(int Kind, string Name, int Count, Color Color);
public sealed record IngredientView(string Name, int Have, int Need);

public sealed record RecipeView(
    string Id,
    string OutputName,
    int Ticks,
    string StationLabel,
    bool CanCraft,
    IReadOnlyList<IngredientView> Ingredients);

public sealed record CraftingView(string RecipeId, long TicksLeft, long Ticks);

/// <summary>游戏 HUD：状态/日志/操作/背包/制作/睡眠。纯 UI 层，事件由 GameRoot 接线。</summary>
public partial class Hud : Control
{
    public event Action? GatherPressed;
    public event Action? AttackPressed;
    public event Action? PickupPressed;
    public event Action? DemolishPressed;
    public event Action<int>? BuildPressed;
    public event Action<int>? BagUsePressed;
    public event Action<int>? BagEquipPressed;
    public event Action<int>? BagDropPressed;
    public event Action<int>? BagSplitPressed;
    public event Action<string>? CraftPressed;
    public event Action? CancelCraftPressed;
    public event Action? SleepPressed;

    private Label? _status;
    private RichTextLabel? _log;
    private GridContainer? _bag;
    private readonly List<int> _slotKinds = new();
    private Button? _bagUse;
    private Button? _bagEquip;
    private Button? _bagDrop;
    private Button? _bagSplit;
    private VBoxContainer? _craftList;
    private Button? _craftCancel;
    private int _selectedSlot = -1;
    private Label? _fps;
    private double _fpsTimer;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _fps = new Label
        {
            Modulate = new Color(0.6f, 1f, 0.7f),
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "FPS --",
            Position = new Vector2(0, 6),
            CustomMinimumSize = new Vector2(80, 22),
        };
        // FPS 标签最后加（最上层），避免被右侧容器盖住

        // 全屏容器布局：左右两列，随窗口缩放自适应
        var root = new MarginContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("margin_left", 16);
        root.AddThemeConstantOverride("margin_top", 12);
        root.AddThemeConstantOverride("margin_right", 16);
        root.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(root);

        var main = new HBoxContainer();
        main.AddThemeConstantOverride("separation", 24);
        root.AddChild(main);

        // 左列：状态 / 操作 / 背包 / 日志（日志弹性占满剩余高度）
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 8);
        left.CustomMinimumSize = new Vector2(420, 0);
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        main.AddChild(left);

        _status = new Label { Modulate = new Color(1f, 0.95f, 0.8f) };
        left.AddChild(_status);

        // 操作按钮行
        var actionBar = new HBoxContainer();
        actionBar.AddChild(MakeButton("采集", () => GatherPressed?.Invoke()));
        actionBar.AddChild(MakeButton("攻击", () => AttackPressed?.Invoke()));
        actionBar.AddChild(MakeButton("拾取", () => PickupPressed?.Invoke()));
        actionBar.AddChild(MakeButton("拆除", () => DemolishPressed?.Invoke()));
        actionBar.AddChild(MakeButton("建火堆", () => BuildPressed?.Invoke(1)));
        actionBar.AddChild(MakeButton("建木墙", () => BuildPressed?.Invoke(2)));
        actionBar.AddChild(MakeButton("睡眠", () => SleepPressed?.Invoke()));
        left.AddChild(actionBar);

        // 背包
        var bagPanel = new VBoxContainer();
        bagPanel.AddThemeConstantOverride("separation", 4);
        bagPanel.AddChild(new Label { Text = "背包" });
        _bag = new GridContainer { Columns = 6 };
        bagPanel.AddChild(_bag);
        var bagBar = new HBoxContainer();
        _bagUse = MakeButton("使用", () => WithBagSlot(_bagUse, BagUsePressed));
        _bagEquip = MakeButton("装备", () => WithBagSlot(_bagEquip, BagEquipPressed));
        _bagDrop = MakeButton("丢弃", () => WithBagSlot(_bagDrop, BagDropPressed));
        _bagSplit = MakeButton("拆分", () => WithBagSlot(_bagSplit, BagSplitPressed));
        bagBar.AddChild(_bagUse);
        bagBar.AddChild(_bagEquip);
        bagBar.AddChild(_bagDrop);
        bagBar.AddChild(_bagSplit);
        bagPanel.AddChild(bagBar);
        left.AddChild(bagPanel);

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            Modulate = new Color(0.95f, 0.9f, 0.8f, 0.9f),
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        left.AddChild(_log);

        // 制作
        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", 8);
        right.CustomMinimumSize = new Vector2(420, 0);
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        main.AddChild(right);

        var craftPanel = new VBoxContainer();
        craftPanel.AddThemeConstantOverride("separation", 4);
        craftPanel.AddChild(new Label { Text = "制作" });
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 160),
        };
        _craftList = new VBoxContainer();
        _craftList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_craftList);
        craftPanel.AddChild(scroll);
        _craftCancel = MakeButton("取消制作", () => CancelCraftPressed?.Invoke());
        _craftCancel.Disabled = true;
        craftPanel.AddChild(_craftCancel);
        right.AddChild(craftPanel);

        AddChild(_fps);
    }

    public override void _Process(double delta)
    {
        _fpsTimer += delta;
        if (_fpsTimer >= 0.3 && _fps is not null)
        {
            _fpsTimer = 0;
            _fps.Text = $"FPS {Engine.GetFramesPerSecond()}";
            // 右上角：随窗口宽度定位（不依赖锚点，最稳）
            _fps.Position = new Vector2(GetViewportRect().Size.X - 96, 6);
        }
    }


    public void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }

    public void Log(string line)
    {
        if (_log is not null) _log.AppendText(line + "\n");
    }

    public void RenderInventory(IReadOnlyList<ItemView> items, int equippedKind, int slots)
    {
        if (_bag is null) return;
        foreach (var child in _bag.GetChildren()) child.QueueFree();
        _slotKinds.Clear();
        for (var i = 0; i < slots; i++)
        {
            var item = i < items.Count ? items[i] : null;
            var btn = new Button
            {
                CustomMinimumSize = new Vector2(46, 46),
                FocusMode = FocusModeEnum.None,
            };
            if (item is { Kind: > 0, Count: > 0 })
            {
                btn.Text = $"{item.Name}\n×{item.Count}" + (equippedKind == item.Kind ? "\n[装]" : "");
                btn.Modulate = item.Color;
                _slotKinds.Add(item.Kind);
            }
            else
            {
                _slotKinds.Add(0);
            }
            var slot = i;
            btn.Pressed += () =>
            {
                _selectedSlot = slot;
                RefreshBagButtons();
            };
            _bag.AddChild(btn);
        }
        RefreshBagButtons();
    }

    public void RenderCraft(IReadOnlyList<RecipeView> recipes, CraftingView? crafting)
    {
        if (_craftList is null) return;
        foreach (var child in _craftList.GetChildren()) child.QueueFree();
        if (crafting is { } c)
        {
            var pct = c.Ticks > 0 ? (int)(100 * (1 - (double)c.TicksLeft / c.Ticks)) : 0;
            var row = new Label { Text = $"制作中：{c.RecipeId}（{pct}%）" };
            _craftList.AddChild(row);
            _craftCancel!.Disabled = false;
            return;
        }

        _craftCancel!.Disabled = true;
        foreach (var r in recipes)
        {
            var row = new VBoxContainer();
            var mat = string.Join("　", r.Ingredients.Select(i => $"{i.Name} {i.Have}/{i.Need}"));
            row.AddChild(new Label
            {
                Text = $"{r.OutputName}（{r.Ticks} ticks）　{r.StationLabel}\n材料: {mat}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            });
            var craft = MakeButton("制作", () => CraftPressed?.Invoke(r.Id));
            craft.Disabled = !r.CanCraft;
            row.AddChild(craft);
            _craftList.AddChild(row);
        }
    }

    private void RefreshBagButtons()
    {
        var has = _selectedSlot >= 0 && _selectedSlot < _slotKinds.Count && _slotKinds[_selectedSlot] > 0;
        if (_bagUse is not null) _bagUse.Disabled = !has;
        if (_bagEquip is not null) _bagEquip.Disabled = !has;
        if (_bagDrop is not null) _bagDrop.Disabled = !has;
        if (_bagSplit is not null) _bagSplit.Disabled = !has;
    }

    private void WithBagSlot(Button? btn, Action<int>? action)
    {
        if (btn is not null && _selectedSlot >= 0) action?.Invoke(_selectedSlot);
    }

    private static Button MakeButton(string text, Action onPressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(64, 32) };
        b.Pressed += onPressed;
        return b;
    }
}
