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

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _status = new Label
        {
            Position = new Vector2(16, 12),
            Modulate = new Color(1f, 0.95f, 0.8f),
        };
        AddChild(_status);

        // 操作按钮行
        var actionBar = new HBoxContainer { Position = new Vector2(16, 108) };
        actionBar.AddChild(MakeButton("采集", () => GatherPressed?.Invoke()));
        actionBar.AddChild(MakeButton("攻击", () => AttackPressed?.Invoke()));
        actionBar.AddChild(MakeButton("拾取", () => PickupPressed?.Invoke()));
        actionBar.AddChild(MakeButton("拆除", () => DemolishPressed?.Invoke()));
        actionBar.AddChild(MakeButton("建火堆", () => BuildPressed?.Invoke(1)));
        actionBar.AddChild(MakeButton("建木墙", () => BuildPressed?.Invoke(2)));
        actionBar.AddChild(MakeButton("睡眠", () => SleepPressed?.Invoke()));
        AddChild(actionBar);

        // 背包
        var bagPanel = new VBoxContainer { Position = new Vector2(16, 152) };
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
        AddChild(bagPanel);

        // 制作
        var craftPanel = new VBoxContainer { Position = new Vector2(420, 12) };
        craftPanel.AddChild(new Label { Text = "制作" });
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(400, 300) };
        _craftList = new VBoxContainer();
        scroll.AddChild(_craftList);
        _craftList.SetAnchorsPreset(LayoutPreset.TopWide);
        craftPanel.AddChild(scroll);
        _craftCancel = MakeButton("取消制作", () => CancelCraftPressed?.Invoke());
        _craftCancel.Disabled = true;
        craftPanel.AddChild(_craftCancel);
        AddChild(craftPanel);

        _log = new RichTextLabel
        {
            Position = new Vector2(16, 540),
            Size = new Vector2(390, 190),
            BbcodeEnabled = true,
            Modulate = new Color(0.95f, 0.9f, 0.8f, 0.9f),
        };
        AddChild(_log);
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
