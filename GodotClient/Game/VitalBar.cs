using System;
using Godot;

namespace GodotClient.Game;

/// <summary>左上两个实心圆：从底部向上填满，数字画在圆心。</summary>
public partial class VitalBar : Control
{
    private const float Diameter = 64f;
    private const float Gap = 10f;
    private const int ArcPoints = 28;

    private HudVitalsViewModel _vitals = HudVitalsViewModel.Create(0, 0, false);

    public VitalBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(Diameter * 2 + Gap, Diameter);
    }

    public void SetVitals(HudVitalsViewModel vitals)
    {
        TooltipText = vitals.IsDead
            ? $"{vitals.Text}　饥饿 {vitals.Hunger}"
            : $"生命 {vitals.Current}/{vitals.Maximum}　饥饿 {vitals.Hunger}/{vitals.HungerMaximum}";
        if (vitals.Signature == _vitals.Signature) return;
        _vitals = vitals;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var healthColor = _vitals.IsDead ? HudTheme.Spirit : HudTheme.Blood;
        DrawOrb(
            new Vector2(Diameter * 0.5f, Diameter * 0.5f),
            _vitals.IsDead ? 0 : _vitals.Ratio,
            healthColor,
            _vitals.IsDead ? "魂" : _vitals.Current.ToString());
        DrawOrb(
            new Vector2(Diameter * 1.5f + Gap, Diameter * 0.5f),
            _vitals.HungerRatio,
            HudTheme.HungerFill,
            _vitals.Hunger.ToString());
    }

    private void DrawOrb(Vector2 center, float ratio, Color color, string label)
    {
        var radius = Diameter * 0.5f - 1.5f;
        DrawCircle(center, radius + 1.2f, new Color(0f, 0f, 0f, 0.55f));
        DrawCircle(center, radius, new Color(0.08f, 0.09f, 0.11f, 0.92f));

        var fill = Mathf.Clamp(ratio, 0, 1);
        var shape = FilledCircle(center, radius - 1f, fill);
        if (shape.Length >= 3)
        {
            DrawColoredPolygon(shape, color);
            DrawColoredPolygon(FilledCircle(center, radius - 1f, Mathf.Max(0, fill - 0.08f)), color.Lightened(0.16f));
        }

        DrawArc(center, radius, 0, Mathf.Tau, 40, new Color(1, 1, 1, 0.18f), 1.4f, true);
        DrawLabel(center, label);
    }

    private void DrawLabel(Vector2 center, string label)
    {
        var font = HudTheme.Font ?? GetThemeDefaultFont();
        if (font is null) return;
        const int size = 20;
        var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, size);
        var pos = center + new Vector2(-textSize.X * 0.5f, textSize.Y * 0.32f);
        foreach (var o in new[] { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1), new Vector2(-1, -1), new Vector2(1, 1) })
            DrawString(font, pos + o, label, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.8f));
        DrawString(font, pos, label, HorizontalAlignment.Left, -1, size, HudTheme.Text);
    }

    private static Vector2[] FilledCircle(Vector2 center, float radius, float ratio)
    {
        ratio = Mathf.Clamp(ratio, 0, 1);
        if (ratio <= 0.004f) return Array.Empty<Vector2>();

        if (ratio >= 0.996f)
        {
            var full = new Vector2[ArcPoints];
            for (var i = 0; i < ArcPoints; i++)
            {
                var a = Mathf.Tau * i / ArcPoints;
                full[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }
            return full;
        }

        var waterY = center.Y + radius * (1f - 2f * ratio);
        var dy = waterY - center.Y;
        var half = Mathf.Sqrt(Mathf.Max(0, radius * radius - dy * dy));
        var start = Mathf.Atan2(dy, half);
        var end = Mathf.Atan2(dy, -half);
        if (end < start) end += Mathf.Tau;

        var steps = Mathf.Max(8, Mathf.CeilToInt(ArcPoints * (end - start) / Mathf.Tau));
        var points = new Vector2[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var a = start + (end - start) * i / steps;
            points[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }
        return points;
    }
}
