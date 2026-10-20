using System.Drawing;

namespace FormatWheel.Sdk;

// Один лепесток колеса.
// Axis — на какую «ось» рецепта влияет выбор (например "format", "size", "quality").
// Value — что записать в эту ось. Разные слоты пишут в разные оси и поэтому дополняют друг друга,
// а не перетирают: можно выбрать и формат, и размер, и качество за один драг.
public sealed record WheelOption(string Axis, object Value, string Label, string Hint = "", Icon? Icon = null)
{
    // лепесток «ничего не менять» (первый в слоте размеров и т.п.)
    public bool IsReset => Value is null;

    public static WheelOption Reset(string axis, string label, string hint = "") => new(axis, null!, label, hint);
}
