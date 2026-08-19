using CommunityToolkit.Mvvm.ComponentModel;

namespace TesserChat.Client.ViewModels;

/// <summary>
/// Base for all view models. Kept deliberately thin — view models are unit tested independently
/// of any view, so nothing here may touch Avalonia UI types.
/// </summary>
public abstract class ViewModelBase : ObservableObject;
