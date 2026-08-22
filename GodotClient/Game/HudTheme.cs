using Godot;

namespace GodotClient.Game;

/// <summary>
/// HUD 视觉令牌：深色玻璃底 + 固定血量红 / 饥饿琥珀。
/// 见仓库根目录 <c>UI美化方案.md</c>。
/// </summary>
public static class HudTheme
{
    public static readonly Color Ink = new("101214");
    public static readonly Color WoodDark = new("16181c");
    public static readonly Color WoodMid = new("22262c");
    public static readonly Color WoodPanel = new("16181ce8");
    public static readonly Color Bronze = new("d8d0c4");
    public static readonly Color BronzeDim = new("6a7078");
    public static readonly Color GoldOld = new("e8e0d4");
    public static readonly Color Parchment = new("f2eee6");
    public static readonly Color ParchmentDim = new("a8a49c");
    public static readonly Color Text = new("f2eee6");
    public static readonly Color Blood = new("e24b4b");
    public static readonly Color Vital = new("e24b4b");
    public static readonly Color Spirit = new("7ec8e8");
    public static readonly Color Danger = new("4a2424");
    public static readonly Color SlotEmpty = new("07090c");
    public static readonly Color SlotWell = new("14181e");
    public static readonly Color SlotRim = new("5a616c");
    public static readonly Color HungerFill = new("e8a033");
    public static readonly Color Ember = new("e8a033");

    public const float SlotSize = 48f;
    public const float ActionSlotSize = 40f;
    public const int VitalSegments = 10;
    public const string FontPath = "res://assets/ui/fonts/SmileySans-Oblique.ttf";

    public static FontFile? Font { get; private set; }

    public static Theme Create()
    {
        var theme = new Theme();
        Font = GD.Load<FontFile>(FontPath);
        if (Font is not null)
        {
            theme.DefaultFont = Font;
            theme.DefaultFontSize = 15;
            theme.SetFont("font", "Label", Font);
            theme.SetFont("font", "Button", Font);
            theme.SetFont("normal_font", "RichTextLabel", Font);
            theme.SetFontSize("font_size", "Label", 15);
            theme.SetFontSize("font_size", "Button", 15);
        }

        theme.SetColor("font_color", "Label", Parchment);
        theme.SetColor("font_shadow_color", "Label", new Color(0f, 0f, 0f, 0.65f));
        theme.SetColor("font_outline_color", "Label", new Color(0f, 0f, 0f, 0.7f));
        theme.SetConstant("shadow_offset_x", "Label", 1);
        theme.SetConstant("shadow_offset_y", "Label", 1);
        theme.SetConstant("outline_size", "Label", 2);
        theme.SetConstant("outline_size", "Button", 2);
        theme.SetColor("font_outline_color", "Button", new Color(0f, 0f, 0f, 0.7f));

        theme.SetColor("default_color", "RichTextLabel", Parchment);
        theme.SetColor("font_shadow_color", "RichTextLabel", new Color(0f, 0f, 0f, 0.55f));

        theme.SetStylebox("normal", "Button", MakeButtonStyle(WoodMid, BronzeDim));
        theme.SetStylebox("hover", "Button", MakeButtonStyle(WoodMid.Lightened(0.1f), GoldOld));
        theme.SetStylebox("pressed", "Button", MakeButtonStyle(WoodDark, GoldOld));
        theme.SetStylebox("disabled", "Button", MakeButtonStyle(Danger.Darkened(0.1f), BronzeDim.Darkened(0.15f)));
        theme.SetStylebox("focus", "Button", MakeButtonStyle(WoodMid, GoldOld));
        theme.SetColor("font_color", "Button", Parchment);
        theme.SetColor("font_hover_color", "Button", GoldOld);
        theme.SetColor("font_pressed_color", "Button", GoldOld);
        theme.SetColor("font_disabled_color", "Button", ParchmentDim.Darkened(0.1f));
        theme.SetColor("font_focus_color", "Button", GoldOld);
        theme.SetConstant("outline_size", "Button", 0);

        theme.SetStylebox("panel", "Panel", MakePanelStyle(WoodPanel, new Color(1, 1, 1, 0.12f)));
        theme.SetStylebox("panel", "PanelContainer", MakePanelStyle(WoodPanel, new Color(1, 1, 1, 0.12f)));

        theme.SetStylebox("background", "ProgressBar", MakeInset(Ink, BronzeDim, 1, 3));
        theme.SetStylebox("fill", "ProgressBar", MakeFlat(Blood, Blood.Lightened(0.15f), 1, 2));
        theme.SetColor("font_color", "ProgressBar", Parchment);

        theme.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());
        var grabber = MakeFlat(BronzeDim, GoldOld, 1, 3);
        grabber.ContentMarginLeft = 2;
        grabber.ContentMarginRight = 2;
        theme.SetStylebox("grabber", "VScrollBar", grabber);
        theme.SetStylebox("grabber_highlight", "VScrollBar", MakeFlat(BronzeDim.Lightened(0.15f), GoldOld, 1, 3));
        theme.SetStylebox("grabber_pressed", "VScrollBar", MakeFlat(GoldOld.Darkened(0.2f), GoldOld, 1, 3));
        theme.SetStylebox("scroll", "VScrollBar", new StyleBoxEmpty());
        theme.SetConstant("padding_left", "VScrollBar", 0);
        theme.SetConstant("padding_right", "VScrollBar", 0);

        return theme;
    }

    public static StyleBoxFlat MakePanelStyle(Color bg, Color border)
    {
        var box = MakeFlat(bg, border, 1, 8);
        box.ShadowColor = new Color(0f, 0f, 0f, 0.28f);
        box.ShadowSize = 4;
        box.ShadowOffset = new Vector2(0, 1);
        box.ContentMarginLeft = 8;
        box.ContentMarginTop = 6;
        box.ContentMarginRight = 8;
        box.ContentMarginBottom = 6;
        return box;
    }

    public static StyleBoxFlat MakeButtonStyle(Color bg, Color border) =>
        MakeFlat(bg, border, 1, 6);

    public static StyleBoxFlat MakeSlotStyle(bool selected)
    {
        var box = MakeInset(SlotEmpty, selected ? GoldOld : SlotRim, selected ? 2 : 1, 6);
        box.ContentMarginLeft = 3;
        box.ContentMarginTop = 3;
        box.ContentMarginRight = 3;
        box.ContentMarginBottom = 3;
        return box;
    }

    /// <summary>代码画背包/制作格：深凹槽 + 亮边，不依赖贴图。</summary>
    public static void DrawSlotWell(CanvasItem canvas, Vector2 size, bool selected, bool hover = false)
    {
        var rim = selected ? GoldOld : hover ? GoldOld.Darkened(0.15f) : SlotRim;
        var outer = new Rect2(0.5f, 0.5f, size.X - 1f, size.Y - 1f);
        canvas.DrawRect(outer, SlotEmpty);
        canvas.DrawRect(new Rect2(3, 3, size.X - 6, size.Y - 6), SlotWell);
        canvas.DrawRect(new Rect2(3, 3, size.X - 6, 2), new Color(0, 0, 0, 0.35f));
        canvas.DrawRect(outer, rim, false, selected ? 2f : 1.25f);
    }

    public static StyleBoxFlat MakeCardStyle() =>
        MakeInset(Ink, new Color(1, 1, 1, 0.1f), 1, 6);

    public static StyleBoxFlat MakeChipStyle(bool ready)
    {
        var box = MakeFlat(
            ready ? new Color("1c3a24") : new Color("3a1c1c"),
            ready ? new Color("5dcc7a") : Blood,
            1,
            5);
        box.ContentMarginLeft = 6;
        box.ContentMarginTop = 2;
        box.ContentMarginRight = 6;
        box.ContentMarginBottom = 2;
        return box;
    }

    public static Color ToneColor(VitalTone tone) => tone switch
    {
        VitalTone.Spirit => Spirit,
        VitalTone.Yellow => HungerFill,
        _ => Blood,
    };

    public static Color Boost(Color color) =>
        color.Lerp(Colors.White, 0.12f).Lightened(0.04f);

    private static StyleBoxFlat MakeInset(Color bg, Color border, int borderWidth, int radius)
    {
        var box = MakeFlat(bg, border, borderWidth, radius);
        box.ShadowColor = new Color(0f, 0f, 0f, 0.4f);
        box.ShadowSize = 2;
        box.ShadowOffset = new Vector2(0, 1);
        return box;
    }

    private static StyleBoxFlat MakeFlat(Color bg, Color border, int borderWidth, int radius)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomRight = radius,
            CornerRadiusBottomLeft = radius,
            ContentMarginLeft = 8,
            ContentMarginTop = 6,
            ContentMarginRight = 8,
            ContentMarginBottom = 6,
        };
    }
}
