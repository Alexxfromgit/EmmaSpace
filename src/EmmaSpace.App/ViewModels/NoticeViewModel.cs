using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EmmaSpace.Core.Notices;

namespace EmmaSpace.App.ViewModels;

/// <summary>A <see cref="Notice"/> on screen, with its buttons wired to the main window's actions.</summary>
public sealed class NoticeViewModel(Notice notice, Func<Notice, NoticeAction, Task> run, Action<Notice>? dismiss)
{
    public Notice Notice { get; } = notice;
    public string Key => Notice.Key;
    public string Title => Notice.Title;
    public string Message => Notice.Message;
    public bool IsWarning => Notice.Level == NoticeLevel.Warning;
    public bool CanDismiss => Notice.CanDismiss && dismiss is not null;

    public IReadOnlyList<NoticeButton> Buttons { get; } =
        notice.Actions.Select(a => new NoticeButton(a.Label, new AsyncRelayCommand(() => run(notice, a)))).ToArray();

    public ICommand DismissCommand { get; } = new RelayCommand(() => dismiss?.Invoke(notice));
}

public sealed record NoticeButton(string Label, ICommand Command);
