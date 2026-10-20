using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FormatWheel.Sdk;

// Что выбрано на всех слотах, по осям. Неизменяемый: выбрали лепесток — получили новый Recipe.
public sealed class Recipe
{
    public static readonly Recipe Empty = new(ImmutableDictionary<string, object>.Empty);

    private readonly ImmutableDictionary<string, object> _values;

    private Recipe(ImmutableDictionary<string, object> values) => _values = values;

    // применить выбор лепестка: reset убирает ось, иначе кладёт значение
    public Recipe With(WheelOption option) =>
        new(option.IsReset ? _values.Remove(option.Axis) : _values.SetItem(option.Axis, option.Value));

    public T? Get<T>(string axis) where T : class => _values.TryGetValue(axis, out var v) ? v as T : null;

    // значение оси как есть, для сравнения «выбран ли этот лепесток»
    public object? GetRaw(string axis) => _values.TryGetValue(axis, out var v) ? v : null;

    public bool Has(string axis) => _values.ContainsKey(axis);

    // чтобы понять, изменился ли рецепт, и не перезапускать ту же конвертацию
    public bool Equals(Recipe other) =>
        _values.Count == other._values.Count && _values.All(kv => other._values.TryGetValue(kv.Key, out var v) && Equals(kv.Value, v));

    public IEnumerable<object> Values => _values.Values;
}
