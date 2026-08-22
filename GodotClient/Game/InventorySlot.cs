using Godot;

namespace GodotClient.Game;

/// <summary>固定背包格：凹槽、亮色块、数量角标、装备角标、选中金框。</summary>
public partial class InventorySlot : Button
{
    private static readonly StyleBoxEmpty Hollow = new();
    private ItemView? _item;
    private bool _equipped;
    private bool _selected;

    public InventorySlot()
    {
        CustomMinimumSize = new Vector2(HudTheme.SlotSize, HudTheme.SlotSize);
        FocusMode = FocusModeEnum.None;
        ToggleMode = false;
        ApplyFrame();
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationMouseEnter or (int)NotificationMouseExit)
            QueueRedraw();
    }

    public void Configure(ItemView? item, bool equipped, bool selected)
    {
        var next = item is { Kind: > 0, Count: > 0 } ? item : null;
        var nextEquipped = equipped && next is not null;
        if (_item == next && _equipped == nextEquipped && _selected == selected) return;
        _item = next;
        _equipped = nextEquipped;
        _selected = selected;
        TooltipText = _item is null ? "" : _item.Name;
        QueueRedraw();
    }

    public override void _Draw()
    {
        HudTheme.DrawSlotWell(this, Size, _selected, IsHovered());
        if (_item is null) return;

        var inset = 7f;
        var body = new Rect2(inset, inset, Size.X - inset * 2, Size.Y - inset * 2);
        if (_item.Icon is not null)
        {
            DrawTextureRect(_item.Icon, body, false);
        }
        else
        {
            var color = HudTheme.Boost(_item.Color);
            var r = Mathf.Min(body.Size.X, body.Size.Y) * 0.44f;
            var mid = body.GetCenter();
            DrawColoredPolygon(
                new[] { mid + new Vector2(0, -r), mid + new Vector2(r, 0), mid + new Vector2(0, r), mid + new Vector2(-r, 0) },
                color.Darkened(0.18f));
            DrawColoredPolygon(
                new[] { mid + new Vector2(0, -r), mid + new Vector2(r, 0), mid },
                color.Lightened(0.22f));
            DrawColoredPolygon(
                new[] { mid + new Vector2(0, -r), mid, mid + new Vector2(-r, 0) },
                color);
        }

        if (_item.Count > 1)
            DrawBadge(new Vector2(Size.X - 20, Size.Y - 15), _item.Count.ToString(), HudTheme.Parchment);
        if (_equipped)
            DrawBadge(new Vector2(5, 4), "装", HudTheme.GoldOld);
    }

    private void ApplyFrame()
    {
        AddThemeStyleboxOverride("normal", Hollow);
        AddThemeStyleboxOverride("hover", Hollow);
        AddThemeStyleboxOverride("pressed", Hollow);
        AddThemeStyleboxOverride("disabled", Hollow);
        AddThemeStyleboxOverride("focus", Hollow);
    }

    private void DrawBadge(Vector2 pos, string text, Color color)
    {
        var font = HudTheme.Font ?? GetThemeDefaultFont();
        if (font is null) return;
        const int size = 11;
        DrawString(font, pos + new Vector2(1, 1), text, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.75f));
        DrawString(font, pos, text, HorizontalAlignment.Left, -1, size, color);
    }
}
