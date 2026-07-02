using System.Collections.ObjectModel;
using DivisonM.Vfs.Engine;

namespace DivisonM.App.ViewModels;

/// <summary>
/// Live activity view ViewModel (FR-UI-MAP): subscribes to the engine's sampled
/// OPS-EVENTS feed and keeps a bounded rolling history so a burst can be reviewed. Under
/// a flood it drops to the feed's already-rate-limited samples and caps its own buffer,
/// so the view never grows unbounded (NFR-UI-LIVE).
/// </summary>
public sealed class ActivityViewModel : ObservableObject, IDisposable {

  private readonly ActivityFeed? _feed;
  private readonly int _maxRows;
  private readonly Action<Action> _dispatch;
  private bool _paused;

  public ActivityViewModel(ActivityFeed? feed = null, int maxRows = 500, Action<Action>? dispatch = null) {
    this._feed = feed;
    this._maxRows = maxRows;
    this._dispatch = dispatch ?? (action => action());
    this.PauseCommand = new(() => this.Paused = !this.Paused);
    this.ClearCommand = new(this.Events.Clear);

    if (feed != null)
      feed.EventPublished += this.OnEvent;
  }

  public ObservableCollection<ActivityEvent> Events { get; } = [];

  public bool Paused {
    get => this._paused;
    set => this.SetProperty(ref this._paused, value);
  }

  public long DroppedSamples => this._feed?.DroppedSamples ?? 0;

  public RelayCommand PauseCommand { get; }
  public RelayCommand ClearCommand { get; }

  public void OnEvent(ActivityEvent activityEvent) {
    if (this.Paused)
      return;

    this._dispatch(() => {
      this.Events.Insert(0, activityEvent);
      while (this.Events.Count > this._maxRows)
        this.Events.RemoveAt(this.Events.Count - 1); // bounded history — never unbounded (NFR-UI-LIVE)

      this.OnPropertyChanged(nameof(this.DroppedSamples));
    });
  }

  public void Dispose() {
    if (this._feed != null)
      this._feed.EventPublished -= this.OnEvent;
  }

}
