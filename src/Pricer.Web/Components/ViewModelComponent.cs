using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.AspNetCore.Components;

namespace Pricer.Web.Components;

/// <summary>Re-renders when the view model, or any collection it exposes, changes. Plays the role of WPF bindings.</summary>
public abstract class ViewModelComponent<T> : ComponentBase, IDisposable where T : INotifyPropertyChanged
{
    private readonly List<INotifyCollectionChanged> _collections = [];

    protected abstract T ViewModel { get; }

    protected override void OnInitialized()
    {
        ViewModel.PropertyChanged += OnChanged;
        foreach (var property in typeof(T).GetProperties())
        {
            if (property.GetIndexParameters().Length == 0 && property.GetValue(ViewModel) is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged += OnCollectionChanged;
                _collections.Add(collection);
            }
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        ViewModel.PropertyChanged -= OnChanged;
        foreach (var collection in _collections) collection.CollectionChanged -= OnCollectionChanged;
        GC.SuppressFinalize(this);
    }
}
