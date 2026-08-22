using Godot;

namespace GodotClient.Game;

/// <summary>制作抽屉格子：固定边长，图标按格子缩放，避免大贴图把栏宽撑爆或裁切。</summary>
public partial class CraftSlot : Button
{
    public const float Cell = 64f;
    private static readonly StyleBoxEmpty Hollow = new();

    private Texture2D? _icon;
    private string _fallback = "?";
    private bool _selected;

    public CraftSlot()
    {
        CustomMinimumSize = new Vector2(Cell, Cell);
        FocusMode = FocusModeEnum.None;
        ToggleMode = true;
        Text = "";
        ApplyFrame();
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationMouseEnter or (int)NotificationMouseExit)
            QueueRedraw();
    }

    public void Configure(Texture2D? icon, string name, bool selected, bool canCraft)
    {
        _icon = icon;
        _fallback = name.Length > 0 ? name[..1] : "?";
        _selected = selected;
        TooltipText = name;
        ButtonPressed = selected;
        Modulate = canCraft ? Colors.White : new Color(1, 1, 1, 0.45f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        HudTheme.DrawSlotWell(this, Size, _selected, IsHovered());
        var inset = 8f;
        var body = new Rect2(inset, inset, Size.X - inset * 2, Size.Y - inset * 2);
        if (_icon is not null)
        {
            DrawTextureRect(_icon, body, false);
            return;
        }

        var font = HudTheme.Font ?? GetThemeDefaultFont();
        if (font is null) return;
        const int size = 22;
        var textSize = font.GetStringSize(_fallback, HorizontalAlignment.Left, -1, size);
        var pos = new Vector2((Size.X - textSize.X) * 0.5f, (Size.Y + textSize.Y) * 0.5f - 2);
        DrawString(font, pos + new Vector2(1, 1), _fallback, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.7f));
        DrawString(font, pos, _fallback, HorizontalAlignment.Left, -1, size, HudTheme.Parchment);
    }

    private void ApplyFrame()
    {
        AddThemeStyleboxOverride("normal", Hollow);
        AddThemeStyleboxOverride("hover", Hollow);
        AddThemeStyleboxOverride("pressed", Hollow);
        AddThemeStyleboxOverride("disabled", Hollow);
        AddThemeStyleboxOverride("focus", Hollow);
    }
}
