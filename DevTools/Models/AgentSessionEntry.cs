using System.Collections.ObjectModel;
using DevTools.Infrastructure;

namespace DevTools.Models;

/// <summary>
/// Bindable, collapsible view of one <see cref="AgentSessionUsage"/>. The session record is replaced every
/// tick, so this wrapper holds the expansion state the record cannot carry and updates its children in
/// place rather than rebuilding the collection, which would collapse the list under the user's cursor.
/// </summary>
public sealed class AgentSessionEntry : ObservableObject
{
    private AgentSessionUsage _session;
    private bool _isExpanded;

    public AgentSessionEntry(AgentSessionUsage session)
    {
        _session = session;
        Members = new ObservableCollection<ProcessUsage>(session.Members);
    }

    public string Key => _session.Key;

    public int RootProcessId => _session.RootProcessId;

    public ObservableCollection<ProcessUsage> Members { get; }

    public string HeadlineText => _session.HeadlineText;

    public string DetailText => _session.DetailText;

    public string SubtitleText => _session.SubtitleText;

    public bool HasSessionTitle => _session.HasSessionTitle;

    public string WorkspaceText => _session.WorkspaceText;

    public double SharePercent => _session.SharePercent;

    public bool IsLongLived => _session.IsLongLived;

    public string ExpandGlyph => IsExpanded ? "−" : "+";

    public string MemberCountText => $"{_session.ProcessCount} process(es)";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RaisePropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    public void UpdateFrom(AgentSessionUsage session)
    {
        _session = session;

        RaisePropertyChanged(nameof(HeadlineText));
        RaisePropertyChanged(nameof(DetailText));
        RaisePropertyChanged(nameof(SubtitleText));
        RaisePropertyChanged(nameof(HasSessionTitle));
        RaisePropertyChanged(nameof(WorkspaceText));
        RaisePropertyChanged(nameof(SharePercent));
        RaisePropertyChanged(nameof(IsLongLived));
        RaisePropertyChanged(nameof(MemberCountText));

        SynchronizeMembers(session.Members);
    }

    private void SynchronizeMembers(IReadOnlyList<ProcessUsage> incoming)
    {
        for (var i = 0; i < incoming.Count; i++)
        {
            if (i < Members.Count)
            {
                Members[i] = incoming[i];
                continue;
            }

            Members.Add(incoming[i]);
        }

        for (var i = Members.Count - 1; i >= incoming.Count; i--)
        {
            Members.RemoveAt(i);
        }
    }
}
