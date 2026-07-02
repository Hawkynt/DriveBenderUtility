using DivisonM.App.Localization;
using DivisonM.Vfs;

namespace DivisonM.App.ViewModels;

/// <summary>
/// Tuning panel ViewModel (§6.13): edits the §8 write/duplication/cache knobs and
/// validates live with the exact rules the CLI uses (CFG-VALIDATE parity, TST-UI). The
/// performance + volatile-ack opt-in surfaces its warning; invalid values are rejected
/// in-form rather than saved.
/// </summary>
public sealed class TuningViewModel : ObservableObject {

  public static readonly IReadOnlyList<WritePolicy> Policies =
    [WritePolicy.WriteThrough, WritePolicy.WriteBack, WritePolicy.Deferred, WritePolicy.Performance];

  public static readonly IReadOnlyList<EvictionPolicy> EvictionPolicies = Enum.GetValues<EvictionPolicy>();

  private WritePolicy _writePolicy = WritePolicy.WriteBack;
  private int _duplication = 2;
  private int _minCopiesBeforeAck = 2;
  private bool _acceptVolatileAck;
  private string _cacheSize = "4GiB";
  private EvictionPolicy _readEviction = EvictionPolicy.Arc;
  private string _validationMessage = "";
  private bool _isValid = true;

  public TuningViewModel() {
    this.ApplyCommand = new(() => this.Validate(), () => this.IsValid);
    this.Validate();
  }

  public WritePolicy WritePolicy {
    get => this._writePolicy;
    set { if (this.SetProperty(ref this._writePolicy, value)) { this.OnPropertyChanged(nameof(this.ShowVolatileWarning)); this.Validate(); } }
  }

  public int Duplication {
    get => this._duplication;
    set { if (this.SetProperty(ref this._duplication, value)) this.Validate(); }
  }

  public int MinCopiesBeforeAck {
    get => this._minCopiesBeforeAck;
    set { if (this.SetProperty(ref this._minCopiesBeforeAck, value)) this.Validate(); }
  }

  public bool AcceptVolatileAck {
    get => this._acceptVolatileAck;
    set { if (this.SetProperty(ref this._acceptVolatileAck, value)) { this.OnPropertyChanged(nameof(this.ShowVolatileWarning)); this.Validate(); } }
  }

  public string CacheSize {
    get => this._cacheSize;
    set { if (this.SetProperty(ref this._cacheSize, value)) this.Validate(); }
  }

  public EvictionPolicy ReadEviction {
    get => this._readEviction;
    set => this.SetProperty(ref this._readEviction, value);
  }

  /// <summary>The GUI must echo the volatility warning whenever RAM-ack is armed (§6.13, SAFE-RAM).</summary>
  public bool ShowVolatileWarning => this.WritePolicy == WritePolicy.Performance && this.AcceptVolatileAck;

  public string VolatileWarning => Localizer.Instance.Get("tuning.volatileWarning");

  public string ValidationMessage {
    get => this._validationMessage;
    private set => this.SetProperty(ref this._validationMessage, value);
  }

  public bool IsValid {
    get => this._isValid;
    private set { if (this.SetProperty(ref this._isValid, value)) this.ApplyCommand.RaiseCanExecuteChanged(); }
  }

  public RelayCommand ApplyCommand { get; }

  /// <summary>Builds the config this panel represents — the same shape the CLI validates.</summary>
  public PoolConfig ToConfig() {
    var json = $$"""
    {
      "duplication": {{this.Duplication}},
      "write": {
        "policy": "{{_PolicyToken(this.WritePolicy)}}",
        "minCopiesBeforeAck": {{this.MinCopiesBeforeAck}},
        "acceptVolatileAck": {{(this.AcceptVolatileAck ? "true" : "false")}}
      },
      "caches": { "global": { "size": "{{this.CacheSize}}", "readEviction": "{{_EvictionToken(this.ReadEviction)}}" } }
    }
    """;
    return ConfigResolver.ResolveEffective(null, json);
  }

  /// <summary>Runs CFG-VALIDATE against the current values; on failure surfaces the precise message and blocks Apply.</summary>
  public bool Validate() {
    try {
      ConfigValidator.Validate(this.ToConfig(), 16L << 30);
      this.ValidationMessage = Localizer.Instance.Get("tuning.valid");
      this.IsValid = true;
    } catch (ConfigValidationException e) {
      this.ValidationMessage = e.Message;
      this.IsValid = false;
    } catch (ManifestException e) {
      this.ValidationMessage = e.Message;
      this.IsValid = false;
    }

    return this.IsValid;
  }

  private static string _PolicyToken(WritePolicy policy) => policy switch {
    WritePolicy.WriteThrough => "write-through",
    WritePolicy.WriteBack => "write-back",
    WritePolicy.Deferred => "deferred",
    WritePolicy.Performance => "performance",
    _ => "write-back",
  };

  private static string _EvictionToken(EvictionPolicy policy) => policy switch {
    EvictionPolicy.Lru => "lru",
    EvictionPolicy.Arc => "arc",
    EvictionPolicy.Fifo => "fifo",
    EvictionPolicy.Lfu => "lfu",
    EvictionPolicy.Clock => "clock",
    EvictionPolicy.ClockPro => "clock-pro",
    EvictionPolicy.Slru => "slru",
    EvictionPolicy.TwoQueue => "2q",
    EvictionPolicy.Mru => "mru",
    _ => "random",
  };

}
