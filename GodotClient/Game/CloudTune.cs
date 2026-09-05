namespace GodotClient.Game;

/// <summary>
/// 体积云构造参数。Default 是主场景现在这套：少云、中等厚度、慢风、低空。
/// </summary>
public readonly record struct CloudTune(
    float Coverage,
    float Thickness,
    float Wind,
    float Height)
{
    public static CloudTune Default { get; } = new(0.22f, 42f, 0.06f, 13f);
}
