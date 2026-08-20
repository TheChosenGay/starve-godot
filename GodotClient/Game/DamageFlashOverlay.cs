using Godot;
using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>全屏本地受击红屏；触发策略与衰减由 DamageFlashState 决定。</summary>
public partial class DamageFlashOverlay : ColorRect
{
    private readonly DamageFlashState _state = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Color = new Color(0.86f, 0.03f, 0.02f, 0);
        Visible = false;
    }

    public bool ApplyImpact(CombatImpactResult result, bool targetsLocalPlayer) =>
        _state.ApplyImpact(result, targetsLocalPlayer);

    public override void _Process(double delta)
    {
        _state.Advance(delta * 1000);
        var alpha = _state.Alpha;
        Visible = alpha > 0.001f;
        Color = new Color(0.86f, 0.03f, 0.02f, alpha);
    }
}
