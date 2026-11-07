using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pricer.Core;

namespace Pricer.Presentation;

public abstract class ParameterField(ParameterDescriptor descriptor, Action<string, object?> commit) : ObservableObject
{
    public string Label { get; } = descriptor.Unit.Length == 0 ? descriptor.Label : $"{descriptor.Label} ({descriptor.Unit})";

    protected void Commit(object? value) => commit(descriptor.Name, value);
}

public sealed class NumberField(ParameterDescriptor descriptor, double initial, bool isInteger, Action<string, object?> commit)
    : ParameterField(descriptor, commit)
{
    private double _value = initial;

    public double Min { get; } = descriptor.Relative ? descriptor.Min * initial : descriptor.Min;
    public double Max { get; } = descriptor.Relative ? descriptor.Max * initial : descriptor.Max;

    public double Value
    {
        get => _value;
        set
        {
            double number = isInteger ? Math.Round(value) : value;
            if (SetProperty(ref _value, number)) Commit(isInteger ? (int)number : number);
        }
    }
}

public sealed class ChoiceField(ParameterDescriptor descriptor, object initial, Action<string, object?> commit)
    : ParameterField(descriptor, commit)
{
    private object _value = initial;

    public Array Choices { get; } = Enum.GetValues(descriptor.ValueType);

    public object Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value)) Commit(value);
        }
    }
}

public sealed class BoolField(ParameterDescriptor descriptor, bool initial, Action<string, object?> commit)
    : ParameterField(descriptor, commit)
{
    private bool _value = initial;

    public bool Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value)) Commit(value);
        }
    }
}

public sealed class DateField(ParameterDescriptor descriptor, DateOnly value, Action<string, object?> commit)
    : ParameterField(descriptor, commit)
{
    private DateTime? _value = value.ToDateTime(TimeOnly.MinValue);

    public DateTime? Value
    {
        get => _value;
        set
        {
            if (value is { } date && SetProperty(ref _value, date)) Commit(DateOnly.FromDateTime(date));
        }
    }
}

/// <summary>Edit form generated from a record's [Parameter] metadata. Each edit replaces the record.</summary>
public sealed class ParameterForm : ObservableObject
{
    public ParameterForm(string title, object record)
    {
        Title = title;
        Record = record;
        foreach (var descriptor in ParameterSchema.Of(record.GetType()))
        {
            object? value = ParameterSchema.Get(record, descriptor.Name);
            Fields.Add(value switch
            {
                double number => new NumberField(descriptor, number, false, Update),
                int number => new NumberField(descriptor, number, true, Update),
                bool flag => new BoolField(descriptor, flag, Update),
                Enum choice => new ChoiceField(descriptor, choice, Update),
                DateOnly date => new DateField(descriptor, date, Update),
                _ => throw new NotSupportedException($"No editor for {descriptor.ValueType.Name}."),
            });
        }
    }

    public event Action? Changed;

    public string Title { get; }
    public object Record { get; private set; }
    public ObservableCollection<ParameterField> Fields { get; } = [];

    private void Update(string name, object? value)
    {
        Record = ParameterSchema.With(Record, name, value);
        Changed?.Invoke();
    }
}
