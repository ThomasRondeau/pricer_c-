using System.Reflection;

namespace Pricer.Core;

/// <summary>Marks a record property as editable. The UI and run diffs are generated from this metadata.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParameterAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    public string Group { get; init; } = "General";
    public string Unit { get; init; } = "";
    public double Min { get; init; } = double.NaN;
    public double Max { get; init; } = double.NaN;

    /// <summary>Min and Max are multiples of the current value, for quantities with no natural scale.</summary>
    public bool Relative { get; init; }
}

public sealed record ParameterDescriptor(
    string Name, string Label, string Group, string Unit, double Min, double Max, bool Relative, Type ValueType)
{
    public bool HasRange => !double.IsNaN(Min) && !double.IsNaN(Max);
}

public sealed record ParameterDifference(ParameterDescriptor Parameter, object? Left, object? Right);

public static class ParameterSchema
{
    public static IReadOnlyList<ParameterDescriptor> Of(Type recordType) =>
        recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Meta: p.GetCustomAttribute<ParameterAttribute>()))
            .Where(x => x.Meta is not null)
            .Select(x => new ParameterDescriptor(
                x.Property.Name, x.Meta!.Label, x.Meta.Group, x.Meta.Unit, x.Meta.Min, x.Meta.Max, x.Meta.Relative,
                x.Property.PropertyType))
            .ToList();

    public static object? Get(object record, string name) => Property(record, name).GetValue(record);

    /// <summary>Returns a copy of the record with one parameter changed; the original is left untouched.</summary>
    public static T With<T>(T record, string name, object? value) where T : class
    {
        var clone = record.GetType().GetMethod("<Clone>$")
            ?? throw new ArgumentException($"{record.GetType().Name} is not a record.");
        var copy = (T)clone.Invoke(record, null)!;
        Property(record, name).SetValue(copy, value);
        return copy;
    }

    public static IReadOnlyList<ParameterDifference> Diff<T>(T left, T right) where T : class =>
        Of(left.GetType())
            .Select(d => new ParameterDifference(d, Get(left, d.Name), Get(right, d.Name)))
            .Where(d => !Equals(d.Left, d.Right))
            .ToList();

    private static PropertyInfo Property(object record, string name) =>
        record.GetType().GetProperty(name)
            ?? throw new ArgumentException($"Unknown parameter '{name}' on {record.GetType().Name}.");
}
