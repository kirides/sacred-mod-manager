using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SacredModManager.ViewModels;

/// <summary>One dot under the viewer.</summary>
public sealed partial class GalleryDotViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

/// <summary>
/// The image viewer (a carousel) of a mod: left / right (arrow keys too) go through the pictures, they are loaded when first shown, the
/// next one is loaded ahead, the viewer wraps around. Escape, the X or a click outside closes it.
/// </summary>
public sealed partial class GalleryViewModel : ObservableObject
{
    private readonly Dictionary<int, Bitmap> _cache = [];
    private Func<int, CancellationToken, Task<Bitmap?>>? _loader;
    private CancellationTokenSource? _cts;

    public ObservableCollection<GalleryDotViewModel> Dots { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Index { get; set; }

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial Bitmap? Current { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool Failed { get; set; }

    public bool HasMany => Count > 1;
    public string CounterText => Count > 1 ? $"{Index + 1} / {Count}" : string.Empty;

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasMany));
        OnPropertyChanged(nameof(CounterText));
    }

    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(CounterText));

    /// <summary>Opens the viewer on picture <paramref name="start"/>; <paramref name="loader"/> loads picture n (null when it cannot be loaded).</summary>
    public void Open(string title, int count, Func<int, CancellationToken, Task<Bitmap?>> loader, int start = 0)
    {
        Close();
        _loader = loader;
        Title = title;
        Count = count;
        Dots.Clear();
        for (int i = 0; i < count; i++)
        {
            Dots.Add(new GalleryDotViewModel(i));
        }

        IsOpen = true;
        _ = ShowAsync(Math.Clamp(start, 0, Math.Max(0, count - 1)));
    }

    [RelayCommand]
    public void Close()
    {
        _cts?.Cancel();
        _cts = null;
        IsOpen = false;
        Current = null;
        _cache.Clear();
        Dots.Clear();
    }

    [RelayCommand]
    public void Next() => Go(1);

    [RelayCommand]
    public void Previous() => Go(-1);

    [RelayCommand]
    public void GoTo(int index)
    {
        if (IsOpen && Count > 0)
        {
            _ = ShowAsync(Math.Clamp(index, 0, Count - 1));
        }
    }

    public void First() => GoTo(0);

    public void Last() => GoTo(Count - 1);

    private void Go(int delta)
    {
        if (IsOpen && Count > 1)
        {
            _ = ShowAsync(((Index + delta) % Count + Count) % Count);
        }
    }

    private async Task ShowAsync(int index)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Index = index;
        foreach (GalleryDotViewModel d in Dots)
        {
            d.IsCurrent = d.Index == index;
        }

        Failed = false;
        if (_cache.TryGetValue(index, out Bitmap? cached))
        {
            Current = cached;
            IsLoading = false;
        }
        else
        {
            Current = null;
            IsLoading = true;
            Bitmap? bitmap = await LoadAsync(index, cts.Token);
            if (cts.IsCancellationRequested || !IsOpen)
            {
                return;
            }

            IsLoading = false;
            Current = bitmap;
            Failed = bitmap is null;
        }

        if (Count > 1)
        {
            _ = LoadAsync((index + 1) % Count, cts.Token); // the next one is ready when the player clicks
        }
    }

    private async Task<Bitmap?> LoadAsync(int index, CancellationToken ct)
    {
        if (_cache.TryGetValue(index, out Bitmap? known))
        {
            return known;
        }

        try
        {
            Bitmap? bitmap = _loader is null ? null : await _loader(index, ct);
            if (bitmap is not null && !ct.IsCancellationRequested && IsOpen)
            {
                _cache[index] = bitmap;
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
