using Godot;

namespace GodotClient.Game;

/// <summary>底栏动作槽：统一深色圆角格 + 单字，完整名称走 Tooltip。</summary>
public partial class ActionSlot : Button
{
    private string _glyph = "";

    public ActionSlot()
    {
        CustomMinimumSize = new Vector2(HudTheme.ActionSlotSize, HudTheme.ActionSlotSize);
        FocusMode = FocusModeEnum.None;
        MouseFilter = MouseFilterEnum.Stop;
        Text = "";
        ApplyFrame();
    }

    public void Configure(string glyph, string tooltip, Color _)
    {
        _glyph = glyph;
        TooltipText = tooltip;
        QueueRedraw();
    }

    public new void SetDisabled(bool disabled)
    {
        base.SetDisabled(disabled);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = HudTheme.Font ?? GetThemeDefaultFont();
        if (font is null || string.IsNullOrEmpty(_glyph)) return;
        const int size = 16;
        var color = Disabled ? HudTheme.ParchmentDim : HudTheme.Text;
        var textSize = font.GetStringSize(_glyph, HorizontalAlignment.Left, -1, size);
        var pos = new Vector2((Size.X - textSize.X) * 0.5f, (Size.Y + textSize.Y) * 0.5f - 1);
        DrawString(font, pos + new Vector2(1, 1), _glyph, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.55f));
        DrawString(font, pos, _glyph, HorizontalAlignment.Left, -1, size, color);
    }

    private void ApplyFrame()
    {
        AddThemeStyleboxOverride("normal", HudTheme.MakeSlotStyle(false));
        AddThemeStyleboxOverride("hover", HudTheme.MakeSlotStyle(true));
        AddThemeStyleboxOverride("pressed", HudTheme.MakeSlotStyle(true));
        AddThemeStyleboxOverride("disabled", HudTheme.MakeSlotStyle(false));
        AddThemeStyleboxOverride("focus", HudTheme.MakeSlotStyle(true));
    }
}
